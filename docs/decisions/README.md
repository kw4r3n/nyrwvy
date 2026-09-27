# Decisions

**Status:** Current

This area records durable rationale for Nyrwvy's current direction. Decision records explain **why** a constraint exists; current behavior and normative contracts belong in reference/specification documents.

## Registers

- [Foundational decisions](foundational.md) — D-001 through D-015, covering graph authority, semantic operations, stable identity, human presentation, backend-neutral lowering, obligations, atomic publication, documentation language, and the F# semantic core.
- [Milestone 1 decisions](milestone-1.md) — accepted implementation-backed refinements covering snapshot authority, ID lifecycle, full validation, operation ordering, approval binding, system-side ID allocation, review facts, physical boundaries, obligation-state growth, persistence replaceability, and language-growth rules.
- [Milestone 2 decisions](milestone-2.md) — accepted rationale for the first bounded structural editing slice: complete Float64 function-body replacement through a separate structural workflow, fresh system-owned local identities, conservative obligation blocking, structural before/after review, and history-preserving publication.
- [Milestone 2 ListMap decision](milestone-2-listmap.md) — accepted M2-D08 extension adding one shallow specialized ListMap complete-body replacement after the Float64 evaluation.
- [RAST provenance](rast-provenance.md) — later accepted refinement of the earlier RAST relationship: no dedicated provenance framework for conceptual ancestry; ordinary attribution applies to concrete copied or substantially adapted material.
- [Effectful expression ordering](effectful-expression-ordering.md) — accepted rationale for scoped Bind expressions, with existing dependency/match ordering and ambiguous-effect rejection preserved.
- [Targeted expression replacement](targeted-expression-replacement.md) — accepted design for all-uses `GraphNodeId` targeting, retained target identity, nested lexical context, and replacement-local pruning.
- [Staged declaration authoring](declaration-authoring.md) — accepted roadmap beginning with atomic function creation and explicit existing-effect requirement edits before type/effect/capability declaration stages.
- [Concurrency control beyond `ParallelAll`](concurrency-control.md) — accepted direction for a structured task scope with individually started, waited and cancelled tasks, a `TaskWait` time limit with a virtual clock in `runPure`, and the shared-capability guarantee; phases 1 (scope, start, wait, cancel) and 2 (time limits with a virtual clock) implemented.
- [Bool operators, and deferring Map](bool-operators-and-map.md) — accepted dedicated short-circuit `BoolAnd`/`BoolOr` and `BoolNot` with checkable conditional expansions; Map, Set and Bool equality deferred with revisit conditions.
- [Open questions](open-questions.md) — bounded uncertainties that remain open, partial, or intentionally deferred.

Where a later accepted decision explicitly refines an earlier one, use the later decision for the refined scope rather than treating both texts as competing current rules.

## Record form

New durable decisions use the shared `Decision / Reason / Consequences / Revisit conditions` form documented in [Documentation conventions](../developer/documentation.md).
