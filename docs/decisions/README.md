# Decisions

**Status:** Current

This area records durable rationale for NyamNyam's current direction and clearly separated proposed decisions under review. Decision records explain **why** a constraint exists; current behavior and normative contracts belong in reference/specification documents.

## Registers

- [Foundational decisions](foundational.md) — D-001 through D-015, covering graph authority, semantic operations, stable identity, human presentation, backend-neutral lowering, obligations, atomic publication, documentation language, and the F# semantic core.
- [Milestone 1 decisions](milestone-1.md) — accepted implementation-backed refinements covering snapshot authority, ID lifecycle, full validation, operation ordering, approval binding, system-side ID allocation, review facts, physical boundaries, obligation-state growth, persistence replaceability, and language-growth rules.
- [Milestone 2 proposed decisions](milestone-2.md) — proposed rationale for the first bounded structural expression-editing slice. These entries are not accepted current behavior until their status changes explicitly.
- [RAST provenance](rast-provenance.md) — later accepted refinement of the earlier RAST relationship: no dedicated provenance framework for conceptual ancestry; ordinary attribution applies to concrete copied or substantially adapted material.
- [Open questions](open-questions.md) — bounded uncertainties that remain open, partial, or intentionally deferred.

Where a later accepted decision explicitly refines an earlier one, use the later decision for the refined scope rather than treating both texts as competing current rules. A proposed decision does not override an accepted decision, current reference, specification, test, or implementation merely because it appears in this directory.

## Record form

New durable decisions use the shared `Decision / Reason / Consequences / Revisit conditions` form documented in [Documentation conventions](../developer/documentation.md).
