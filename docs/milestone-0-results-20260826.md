# Milestone 0 results — 2026-08-26

## Decision

**Milestone 0 passes as an architectural proof.**

The representative Nayamnyam program is created and persisted as a typed semantic graph, edited through semantic transactions with stable identity, reviewed through graph-derived projections, lowered through two backend-neutral stages, emitted as deterministic C#, compiled, and executed on .NET. The required incomplete-signature lifecycle is represented explicitly through blocking obligations rather than guessed values.

This is not a claim that the language is production-ready. The result is specifically that the Milestone 0 architecture is coherent enough to justify proceeding to Milestone 1.

## Final verification baseline

Exact Phase O code head:

```text
b74de0d660a39dc327a8c364c7ae937720cf0123
```

Verified locally from a clean `bin/obj` state with .NET SDK `10.0.111`:

```text
Release build:  0 warnings / 0 errors
Full suite:     93 passed / 0 failed / 0 skipped
M0 E2E:          1 passed / 0 failed / 0 skipped
```

See `rebuild-phase-o-m0-e2e-20260826.md` for the full focused-test evidence.

## Exit criteria audit

### 1. Graph authority — PASS

The representative program is constructed directly as semantic graph data in the F# fixture; no human-authored Nayamnyam source parser participates in the lifecycle. Snapshot JSON/Nyason, viewer HTML, Typed Semantic IR, Execution IR, and generated C# are downstream representations.

The Phase O test persists and reloads the graph canonically before any execution and then continues the entire lifecycle from the reloaded snapshot.

### 2. Identity continuity — PASS

Types, fields, variants, functions, parameters, graph nodes, effects, capabilities, obligations, transactions, and snapshots use typed stable-ID domains.

The required rename changes `validate_user` to `validate_profile` while preserving the same `FunctionId`. Direct callers continue targeting the function identity without textual reference replacement. Signature evolution adds a new stable `ParameterId` and resolves the affected call by explicit parameter identity.

### 3. Atomic change — PASS

`Transaction.simulate` is pure with respect to the supplied snapshot. `SemanticWorkspace` adds explicit `simulate`, conservative `commit`, policy-gated incomplete publication, and explicit `reject` actions around that same semantic operation engine.

Rejected transactions leave the exact authoritative workspace unchanged. Stale-base publication and reuse of an already-published snapshot identity are rejected.

### 4. Explicit incompleteness — PASS

`function.add_parameter` never invents a call-site value. A caller may deliberately receive a typed `MissingArgument` obligation. The proposed graph remains structurally valid while `SnapshotReadiness` and workspace readiness classify it as incomplete.

Default `commit` rejects blocking obligations. Publishing an incomplete snapshot requires the explicit `AllowBlockingObligations` policy. Typed Semantic IR projection is blocked until the obligation is resolved by a later semantic operation.

### 5. Human observability — PASS for the M0 prototype

Phase K derives read-only Focus and Change Review projections directly from the authoritative snapshot/semantic diff. Phase L converts those projections to typed viewer models and deterministic, HTML-escaped static HTML. Phase M exports those artifacts through a strict stable-ID-only CLI.

The viewer exposes function signatures, structured branches, Result success/failure paths, callers/callees, dependency relations, capability/effect declarations, affected functions, semantic diff, readiness, and blocking obligations. Meaning does not depend on color alone and the view is structurally nested rather than a full-repository force-directed graph.

**Prototype caveat:** the current Change Review communicates operation impact primarily through `SemanticDiff` plus transaction identity. A first-class human-readable rendering of the original requested operation intent would improve review ergonomics in Milestone 1.

### 6. Executable semantics — PASS

Validated obligation-free snapshots project to Typed Semantic IR, lower to explicit-order Execution IR, emit deterministic C#, compile in a separate temporary .NET project, and execute successfully.

The Phase O lifecycle executes representative inputs three times: initial state, after stable-ID-preserving rename, and after obligation resolution. All produce:

```text
valid user
empty name
invalid email
```

Open blocking obligations prevent lowering.

### 7. Determinism — PASS within the defined M0 boundary

The project has deterministic/canonical tests for snapshot serialization, Typed Semantic IR serialization, Execution IR serialization, C# emission, viewer models/artifacts, semantic diff ordering, and diagnostic/obligation ordering where applicable.

The E2E test proves canonical snapshot bytes reproduce after round-trip and that generated C# source reproduces for the identical resolved snapshot.

Non-semantic collection order in in-memory F# records is intentionally not authority; canonical serialization normalizes it.

### 8. Architectural isolation — PASS by dependency direction, with a packaging caveat

The semantic graph/foundation modules do not consume viewer HTML, generated C#, or RAST. The dependency direction is downstream:

```text
semantic graph
  -> query / transaction / validation
  -> IR
  -> backend

semantic graph / query
  -> viewer projection
  -> HTML artifact
```

No generated C# is parsed back into semantic state and RAST is not a runtime dependency.

**Packaging caveat:** several downstream layers currently live in the same `Nayamnyam.Core` .NET assembly for rebuild convenience. Their source dependency direction is correct, but separate projects/assemblies would make the boundary mechanically stronger and should be considered in Milestone 1.

### 9. Structured failure — PASS

Failure modes are typed and separated:

- graph/operation rejection through transaction diagnostics;
- semantic incompleteness through explicit obligation IDs/readiness;
- Typed Semantic IR refusal for incomplete snapshots;
- Execution IR lowering errors for ambiguous effect order and invalid operational structure;
- backend readiness errors such as missing capability bindings;
- viewer/export decode/query errors.

The workspace deliberately uses `SemanticallyReady` rather than `Executable`, because backend binding readiness belongs to a later layer.

### 10. Documented learning — PASS

The rebuild now has phase records for the semantic foundation, Nyason experiment, both IR layers, backend, query projections, viewer, artifact export, transaction publication, and complete lifecycle.

The most important learning from Phase O was that canonical persistence equality must be defined in semantic/canonical terms rather than raw in-memory list ordering. The E2E test exposed and corrected that test assumption without changing codec behavior.

## Architectural assumptions that held

- Stable identity works better than names as the edit/reference anchor.
- One authoritative call representation is sufficient; a second `Calls` collection was unnecessary.
- Explicit specialized semantic node kinds avoided backend/query ambiguity in the M0 domain.
- Missing information can remain structurally valid as typed obligations without guessed defaults.
- Semantic graph, typed meaning, execution order, backend binding, and human presentation can remain distinct layers.
- A backend can consume Execution IR only without reparsing graph persistence or repeating semantic inference.
- Local query projections are sufficient to drive a human viewer without making the viewer an authority.
- Conservative effect ordering is practical: ambiguous independent effect order is rejected rather than guessed.

## Assumptions refined during the rebuild

### Canonical equality is not raw record equality

Canonical serializers may sort non-semantic collections. Therefore persistence identity is defined by canonical bytes and stable semantic identities, not by incidental F# list order.

### Semantic readiness is not backend executability

A graph may be semantically complete while a chosen backend profile is still missing a capability binding. The workspace therefore stops at `SemanticallyReady`; executable readiness remains a composed later-stage property.

### Human and AI projections should not duplicate authority

Focus/Change Review payloads, typed viewer models, HTML, and exported artifact filenames are all derived and discardable. They must not become alternate sources of semantic truth.

## Milestone 1 carry-over risks / open work

These do not invalidate the M0 architecture proof, but they are the highest-value follow-ups:

1. **Production stable-ID allocation (Q-002).** Tests use deterministic opaque IDs; production allocation should implement the accepted domain-prefix + UUIDv7 direction.
2. **Persistence choice (Q-001).** Nyason v0 is mechanically strict but currently ~1.920x the compact JSON size on M0. It still needs AI comprehension/edit experiments before becoming canonical.
3. **Physical project boundaries.** Split semantic foundation, viewer/export, and backend into separate assemblies/packages if that improves mechanical isolation without creating excessive ceremony.
4. **Richer Change Review intent.** Preserve/render explicit requested operation intent in addition to the derived semantic diff when useful for human approval.
5. **Viewer ergonomics.** Add an interactive detail drawer/navigation layer only if it can consume the existing typed viewer model without becoming semantic state.
6. **Hosted CI capacity.** Later phases relied on clean local verification because GitHub Actions capacity was exhausted; restore hosted verification when available.
7. **Broader language surface.** M0 deliberately covers only the small validation slice. Milestone 1 should expand semantics selectively rather than generalizing every construct at once.

## Milestone 0 conclusion

The central claim survived the vertical slice:

> A persistent typed semantic graph can be the authoritative program, can be changed safely through meaning-level operations, can represent unresolved work explicitly, can support focused human review, and can deterministically lower to an ordinary execution platform without authoritative source text.

Proceeding to Milestone 1 is justified. The next milestone should preserve the same authority and dependency rules rather than treating the M0 implementation as a prototype to be replaced wholesale.