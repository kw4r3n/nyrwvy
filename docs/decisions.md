# Foundational Decisions

This register records decisions that define NyamNyam's current direction. They are accepted architectural constraints, not implementation details. They may be revisited only when concrete design or implementation evidence shows that a decision prevents the project from meeting its goals.

## D-001 — The semantic graph is the source of truth

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

The authoritative program representation is a persistent typed semantic graph. Textual code, generated backend source, viewer diagrams, reports, and serialized debug views are projections.

### Reason

The project requires stable identity, explicit relationships, transactional changes, and direct semantic queries. Treating source text as authoritative would force the system to repeatedly reconstruct those facts and would preserve the failure modes NyamNyam is intended to remove.

### Consequences

- The core must support program construction without parsing source text.
- Serialization is storage infrastructure, not a human authoring contract.
- Projection regeneration must not change semantic identity.
- Generated C# must never be treated as an editable upstream source.

### Revisit conditions

Revisit only if a graph-authoritative model proves incompatible with reliable version control, recovery, or deterministic builds and no graph-based remedy is viable.

---

## D-002 — Changes are semantic operations, not text patches

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

AI agents and future human editing tools request typed semantic operations against stable targets. They do not directly mutate graph storage or submit arbitrary source patches as the primary change mechanism.

### Reason

One conceptual change often requires coordinated updates across declarations, references, call sites, types, effects, and tests. The system should own that propagation and validation.

### Consequences

- Operation schemas are versioned public contracts.
- Operations declare or compute preconditions, impact, and graph deltas.
- A transaction simulator and semantic diff are required.
- Raw storage mutation remains an internal recovery or migration mechanism only.

### Revisit conditions

Revisit the exclusivity of semantic operations if some classes of local expression editing cannot be represented practically. Such a revision must still preserve transactional validation and stable identity.

---

## D-003 — Stable IDs define identity

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

Names, paths, source positions, serialized order, and viewer coordinates are not semantic identity. Relevant program entities receive opaque stable IDs.

### Reason

Rename, movement, parameter reordering, and projection changes must not require heuristic identity reconstruction.

### Consequences

- References point to IDs rather than names.
- Argument relationships should bind to parameter IDs rather than relying only on position.
- Names become mutable display properties.
- Copy, fork, import, and cross-repository ID semantics require explicit policy.

### Revisit conditions

The specific ID encoding may change freely. The separation between identity and display naming should not be revisited without evidence that persistent identity itself is harmful.

---

## D-004 — Human presentation is graph-first

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

The primary human interface is a graph-oriented viewer for program structure, flow, effects, change impact, and obligations. A complete code-like listing is secondary.

### Reason

Humans usually need to understand relationships and consequences rather than manually reconstruct them from a linear serialization. The semantic graph already contains the relationships a conventional code reader must infer.

### Consequences

- Viewer design is part of the first end-to-end milestone.
- Graph slicing and semantic layout are core product concerns.
- Local expressions may use structured text when graph expansion reduces clarity.
- The viewer must avoid presenting the entire repository as one undifferentiated graph.

### Revisit conditions

Revisit the balance of graph and textual views if empirical use shows that a particular domain is consistently clearer in text. This does not change the graph as the source of truth.

---

## D-005 — Direct human source authoring is not a primary requirement

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

The core representation and operation model are optimized first for safe AI manipulation and human supervision. Manual source typing convenience is not a leading design constraint.

### Reason

Requiring one surface syntax to serve storage, AI editing, human authoring, review, and execution would force compromises that weaken the semantic model.

### Consequences

- A human-readable projection may exist without being round-trip authoritative.
- A future textual import format is optional.
- Humans initially request changes, review transactions, and resolve obligations rather than directly writing program files.

### Revisit conditions

A constrained textual editing interface may be added if it maps cleanly to semantic operations and does not create a second source of truth.

---

## D-006 — NyamNyam is an independent programming system

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

NyamNyam is not implemented as a macro layer, annotation system, or DSL embedded in C#, Kotlin, Rust, or another host language.

### Reason

A host-language extension would make the host AST and semantics primary, weaken persistent identity, permit untracked escape hatches, and make the graph a derived view again.

### Consequences

- Core semantics are specified independently of backend languages.
- External code is accessed through typed capability contracts.
- Backend code is generated from validated IR.
- Existing language tooling is used behind explicit boundaries rather than as the language definition.

### Revisit conditions

Embedding adapters may be added for integration, but they must not become the authoritative representation or bypass the semantic operation model.

---

## D-007 — Lowering uses backend-neutral IR boundaries

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

The semantic graph lowers through a Typed Semantic IR and a backend-neutral Execution IR before target-specific generation.

### Reason

Direct graph-to-C# generation would encourage C# representation choices to leak into the core and would make future backend work difficult to evaluate.

### Consequences

- Editing metadata and operation history do not enter executable IR unless needed for provenance.
- Evaluation order and execution behavior become explicit before target generation.
- Backend feature limitations produce lowering diagnostics.
- IR schemas require independent versioning and tests.

### Revisit conditions

The two IR layers may merge if implementation shows no meaningful separation, but the backend-neutral boundary must remain.

---

## D-008 — C#/.NET is the first execution backend

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

Milestone 0 targets deterministic C# generation, .NET compilation, and execution.

### Reason

.NET offers managed memory, generics, practical cross-platform execution, external interoperability, and a future Native AOT path without forcing ownership and borrowing semantics into the first core model.

### Consequences

- The initial runtime and external Console capability are implemented for .NET.
- Generated C# is a build artifact.
- Kotlin is not an initial backend.
- Rust remains a future backend candidate for testing non-GC and ownership-sensitive lowering.

### Revisit conditions

Revisit if .NET cannot express the accepted Milestone 0 semantics without backend-specific compromises in the core.

---

## D-009 — Unresolved work is represented by explicit obligations

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

A structurally valid graph may contain typed blocking obligations. Obligations represent missing decisions or required repairs and prevent lowering at their declared blocking stage.

### Reason

Large semantic changes can be meaningful before every domain decision is resolved. Rejecting all incomplete changes is too restrictive, while silently generating placeholder or guessed behavior is unsafe.

### Consequences

- Graph validity and execution readiness are distinct states.
- Transactions may commit obligations only under explicit policy.
- Obligations have stable identity, provenance, affected entities, and resolution history.
- Viewer and query APIs treat obligations as first-class data.

### Revisit conditions

Obligation categories and blocking stages may evolve. The principle that unresolved semantics must be explicit should remain.

---

## D-010 — Transactions are atomic at the authoritative snapshot boundary

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

A transaction applies all accepted operations to one base revision and either commits one structurally valid new snapshot or commits nothing.

### Reason

Partial mutation makes failure recovery ambiguous and allows agents to leave the program in a state that was never reviewed as a whole.

### Consequences

- Optimistic revision checks or equivalent base-snapshot validation are required.
- Simulation and preview use the same transformation and validation rules as commit.
- Graph-integrity failures always reject.
- Incomplete commits require explicit obligations rather than malformed structures.

### Revisit conditions

Internal storage may use incremental writes, but observable atomicity at the program snapshot boundary must remain.

---

## D-011 — The initial viewer is read-only

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

Milestone 0's viewer supports exploration, flow understanding, semantic diff review, and obligation inspection. It does not construct or edit programs visually.

### Reason

The first question is whether the graph model helps humans understand and supervise AI-driven changes. Visual editing would add a separate interaction-design problem before the semantic operation layer is proven.

### Consequences

- Changes originate through test fixtures, CLI/API clients, or AI-facing operation requests.
- Approval and rejection may be represented in the review UI, but graph mutation remains outside the viewer's initial scope.
- Human semantic editing may be added later using the same operation APIs.

### Revisit conditions

Add limited editing after operation schemas, transaction behavior, and review workflows are stable.

---

## D-012 — Milestone 0 includes one explicit effect and typed external capability

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

The first end-to-end slice includes a declared Console output effect exposed through a typed external capability. Async, filesystem, network, time, randomness, and shared mutable state are excluded.

### Reason

A pure-only prototype would postpone effect and external-boundary design, both of which materially shape the graph and viewer. One capability is enough to validate the boundary without expanding scope excessively.

### Consequences

- Effect declarations and capability requirements are part of the first graph schema.
- Execution-readiness validation checks for a backend binding.
- The viewer shows the effect edge.
- The C# backend supplies the first external adapter.

### Revisit conditions

The exact Console contract may change, but Milestone 0 should retain one explicit external effect.

---

## D-013 — The previous RAST repository is a reference, not a dependency

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

NyamNyam is implemented in a new repository. RAST provides design history, semantic rules, examples, tests, and migration input, but NyamNyam's semantic core does not import or depend on the RAST implementation.

### Reason

RAST assumes a text-first parser-to-AST pipeline. Reusing that implementation as the core would preserve incompatible architectural assumptions.

### Consequences

- Migrated concepts and tests should record source provenance and reference revision.
- A legacy importer may be built later.
- Compatibility adapters must remain outside the semantic core.
- RAST can continue to serve as a behavioral comparison during migration.

### Revisit conditions

Shared standalone utility code may be copied with provenance if it has no dependency on the old AST architecture. No runtime dependency should be introduced merely for convenience.

---

## D-014 — Project documentation is authored in English

- **Status:** Accepted
- **Date:** 2026-07-28

### Decision

Canonical repository documentation, schemas, diagnostics, and design terminology are written in English.

### Reason

English provides consistent terminology for implementation tools, AI agents, external references, and future collaboration. Japanese translations can be produced when useful without becoming competing specifications.

### Consequences

- A translation must identify the canonical English source and revision.
- Schema field names and stable diagnostic codes use English.
- Discussion may occur in other languages, but accepted decisions are recorded in English.

### Revisit conditions

None expected. Additional translated guides may be added as non-canonical projections.

---

## D-015 — F# is the initial semantic-core implementation language

- **Status:** Accepted
- **Date:** 2026-08-23
- **Evidence:** Architecture review and product decision on 2026-08-23. This decision does not claim that the planned controlled F# versus C# spike has been completed.

### Decision

The authoritative semantic graph, validation and operation engine, transaction model,
obligation model, semantic diff model, and backend-neutral IR are initially
implemented in F#.

C#/.NET remains the first execution target. The initial C# emitter may be
implemented in F# and produces deterministic C# source. If a separately compiled
C# backend component is introduced later, it must consume an explicit backend
contract or canonical execution-IR format rather than internal F# union
representations.

### Reason

The semantic core is primarily a closed, immutable domain with typed alternatives:
graph entities, operation requests, transaction outcomes, obligations, diagnostics,
semantic diffs, and IR nodes. F# records, discriminated unions, Option, Result, and
explicit pattern matching model these concepts directly and make unhandled cases
visible during maintenance.

The execution target and the semantic-core implementation language are independent
under the accepted backend-neutral IR boundary. Choosing F# for the core therefore
does not reduce access to .NET compilation, C# interoperability, generated C#
runtime adapters, or future Native AOT experiments for generated programs.

### Consequences

- Core code uses immutable records at public domain boundaries and discriminated
  unions for closed semantic states.
- Domain pattern matches enumerate cases explicitly; wildcard branches must not
  absorb unknown domain cases.
- Custom operators, active patterns, dense point-free code, and hidden
  serialization conventions are not part of the core style.
- Canonical graph and IR persistence use explicit, versioned codecs rather than
  automatic serialization of internal F# representations.
- C#-facing component boundaries must not expose F# discriminated unions directly.
- Generated C# and its runtime support remain build artifacts and must not depend
  on F# types unless a future backend decision explicitly accepts that dependency.

### Revisit conditions

Revisit this choice if implementation evidence shows that F# causes repeated
maintenance failures that cannot be addressed by the core style constraints, if
C#-facing boundary adapters become a dominant cost, or if the core becomes
substantially dominated by performance-sensitive mutable infrastructure that cannot
remain safely encapsulated behind a functional interface.
