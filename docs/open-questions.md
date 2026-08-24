# Open Questions

These questions concern implementation and representation details. They do not weaken the accepted direction in [Foundational Decisions](decisions.md).

Each question includes a resolution target so that uncertainty remains bounded rather than becoming an implicit placeholder.

## Q-001 — What is the canonical persistence format?

**Status: Resolved for Milestone 0.** See [Graph Schema v1](schema-v1.md).

### Question

Should an authoritative snapshot be stored as canonical text, structured JSON-like documents, a custom binary format, an embedded database, or a combination of these?

### Decision criteria

- deterministic serialization;
- atomic snapshot publication;
- partial-corruption detection and recovery;
- efficient focused queries;
- practical Git storage and diff behavior;
- schema migration support;
- implementation simplicity for Milestone 0;
- no requirement for humans to edit storage directly.

### Resolution target

Resolve before the Milestone 0 graph store is implemented. A replaceable storage interface is still required so that the first choice does not become the semantic model.

---

## Q-002 — How are stable IDs generated and scoped?

**Status: Resolved for Milestone 0.** See [Graph Schema v1](schema-v1.md).

### Question

What encoding and allocation strategy should be used for entity, node, obligation, transaction, and snapshot IDs?

### Decision criteria

- low collision risk;
- deterministic testing support;
- safe concurrent creation;
- compact transport and indexing;
- opaque semantics;
- clear behavior when importing, copying, templating, forking, or merging programs.

### Resolution target

Resolve basic generation and local scope before the first graph entity is persisted. Cross-repository copy and merge semantics may remain deferred beyond Milestone 0 if explicitly unsupported.

---

## Q-003 — What is the relationship between snapshots and operation logs?

### Question

Should the current graph snapshot be the sole canonical state with optional audit logs, should state be reconstructed from operations, or should both be canonical with consistency checks?

### Decision criteria

- reliable recovery;
- efficient startup and queries;
- explainable provenance;
- operation-schema evolution;
- Git friendliness;
- transaction replay determinism;
- ability to compact history without changing program identity.

### Resolution target

Milestone 0 may use canonical snapshots plus an append-only audit log. The long-term canonicality of the log should be decided after measuring replay and migration complexity.

---

## Q-004 — Which structures are first-class graph entities?

**Status: Resolved for Milestone 0.** See [Graph Schema v1](schema-v1.md).

### Question

Declarations clearly need stable identity, but which local expressions, control-flow nodes, pattern arms, and literal values should be persistent entities rather than embedded values?

### Decision criteria

An element should receive stable identity when at least one of the following is important:

- operations target it independently;
- diagnostics or obligations must survive unrelated edits;
- queries reference it across projections;
- semantic diffs should track continuity;
- provenance or test coverage attaches to it.

Excessive identity should be avoided when it creates graph noise without operational value.

### Resolution target

Define the Milestone 0 entity boundary before the graph schema is finalized. Re-evaluate using real operation and viewer requirements.

---

## Q-005 — How are operation composition and conflicts specified?

### Question

Should operations publish static read/write sets, compute them during simulation, use semantic conflict rules, or combine these methods?

### Decision criteria

- conservative conflict detection;
- explainable rejection;
- support for commuting operations such as rename and parameter reorder;
- avoidance of false confidence from field-level write-set comparison;
- compatibility with future multi-agent plans;
- operation-schema versioning.

### Resolution target

Milestone 0 requires deterministic sequential composition and explicit base-revision checks. General commutativity and automatic reordering may remain post-Milestone 0, but the operation model must not prevent them.

---

## Q-006 — What is the exact obligation state machine?

### Question

Which statuses, blocking stages, ownership rules, and resolution mechanisms should obligations support?

### Candidate minimum

- `open`;
- `resolved`;
- `superseded`;
- `invalidated`.

Potential blocking stages include semantic validation, execution lowering, backend binding, and deployment.

### Decision criteria

- obligations cannot silently disappear;
- resolution is attributable to a transaction;
- stale obligations are detectable after affected graph changes;
- candidate repairs are structured but not treated as decisions;
- the viewer can group obligations by cause and impact.

### Resolution target

Resolve the minimum state machine before implementing the Milestone 0 signature-change demonstration.

---

## Q-007 — How is incremental validation organized?

### Question

Should validators recompute all facts from each snapshot, use dependency-tracked incremental recomputation, or combine full and incremental modes?

### Decision criteria

- correctness before optimization;
- deterministic results independent of cache state;
- explicit invalidation rules;
- ability to explain which changes caused which diagnostics;
- acceptable response time for viewer and transaction preview.

### Resolution target

Milestone 0 may use full recomputation over a small graph. Validator APIs and derived-fact storage must still be designed so incremental evaluation can be introduced later.

---

## Q-008 — How should graph slices and layouts be defined?

### Question

What query and layout contracts produce understandable views without reducing the viewer to a generic graph renderer?

### Decision criteria

- relation-specific layout;
- focused depth and ownership boundaries;
- stable placement across small changes;
- progressive disclosure;
- explicit handling of cycles;
- accessibility without color-only distinctions;
- rendering performance;
- correspondence between semantic diff and displayed impact.

### Resolution target

Resolve a minimal Focus View and Change Review View contract before implementing the Milestone 0 viewer. Broader workspace visualization remains later work.

---

## Q-009 — Where is the boundary between Typed Semantic IR and Execution IR?

### Question

Which information belongs in each IR, and can the two be represented by one schema with staged invariants or should they be distinct schemas?

### Decision criteria

- editing-only metadata does not leak into backend logic;
- execution order is explicit before target generation;
- backend-neutral semantics are testable independently;
- diagnostics retain graph provenance;
- future backend differences are isolated;
- unnecessary translation layers are avoided.

### Resolution target

Resolve before C# generation begins. The two conceptual stages must remain visible even if Milestone 0 uses closely related data structures.

---

## Q-010 — How strict is an external capability contract?

### Question

Beyond input, output, failure type, and effect, which properties must an external capability declare?

Potential properties include:

- determinism;
- idempotency;
- thread safety;
- blocking behavior;
- cancellation behavior;
- resource ownership;
- serialization requirements;
- sandbox permissions;
- backend availability.

### Decision criteria

The contract should expose facts needed for validation and planning without requiring speculative metadata that no component uses.

### Resolution target

Define only the properties required by the synchronous Console capability in Milestone 0. Add new fields when a validator, planner, backend, or viewer has a concrete use for them.

---

## Q-011 — How should core types map to C#?

### Question

What C# representations should be used for product types, unions, `Option`, `Result`, `Unit`, immutable collections, and capabilities?

### Decision criteria

- deterministic generation;
- clear runtime semantics;
- no dependence on generated source being manually readable;
- reasonable type safety;
- straightforward .NET compilation;
- future Native AOT compatibility where practical;
- diagnostics traceable to graph IDs;
- representation decisions remain backend-local.

### Resolution target

Resolve the Milestone 0 subset before the emitter is implemented. Performance-specialized layouts are deferred.

---

## Q-012 — What ownership information should the core eventually express?

### Question

Should Nayamnyam remain defined around managed shared values, add ownership and borrowing semantics, or model abstract usage constraints that backends can lower differently?

### Decision criteria

- AI can apply ownership-sensitive changes reliably;
- managed backends are not burdened with unnecessary ceremony;
- a future Rust backend does not require arbitrary cloning or runtime-managed wrappers everywhere;
- concurrency and resource safety can be expressed explicitly;
- the model remains understandable in graph views.

### Resolution target

Post-Milestone 0. The initial core uses managed semantics and must avoid claiming backend-independent zero-cost ownership guarantees.

---

## Q-013 — How is provenance recorded when reusing RAST material?

### Question

What metadata or documentation convention should link migrated semantic rules, examples, and tests to the RAST repository and source revision?

### Decision criteria

- easy auditability;
- no runtime coupling;
- clear distinction between copied behavior and newly designed behavior;
- manageable maintenance when Nayamnyam intentionally diverges.

### Resolution target

Define a lightweight provenance convention before the first RAST-derived test or rule is imported.

---

## Q-014 — How should Nayamnyam integrate with Git?

### Question

What files should be committed, and what constitutes a useful code review when the authoritative form is a graph snapshot?

Possible committed artifacts include:

- canonical graph snapshots;
- semantic transaction records;
- schema/version manifests;
- human-readable semantic diff summaries;
- deterministic test fixtures;
- generated backend source only for debugging or release artifacts.

### Decision criteria

- ordinary clone and checkout produce a complete program;
- merges do not silently destroy identity;
- reviews expose semantic change clearly;
- generated churn is minimized;
- repository corruption is detectable;
- tooling does not require a centralized service.

### Resolution target

Milestone 0 must define a reproducible committed snapshot format and semantic-diff test output. General semantic merge support may remain later work.

---

## Q-015 — How is approval represented?

### Question

Is human approval external workflow metadata, a signed transaction property, a repository policy, or a first-class graph-adjacent record?

### Decision criteria

- approval does not alter program semantics accidentally;
- the approved diff is bound to an exact base and proposed snapshot;
- stale approval is rejected after changes;
- automated and manual policies can coexist;
- audit history is available without forcing approval data into executable IR.

### Resolution target

Milestone 0's viewer may show approve/reject as a prototype workflow without defining cryptographic or organizational policy. Exact snapshot binding is required if approval triggers commit.
