# Open questions

**Status:** Current

This register tracks implementation and representation questions that remain genuinely open, partially resolved, or intentionally deferred. Resolved historical questions are summarized at the end rather than kept as apparent blockers.

Open questions do not weaken the accepted direction in the [decision registers](README.md). Implementations must use the accepted current position where one exists and must not silently guess the unresolved remainder.

## Q-001 — What becomes the permanent authoritative persistence medium?

- **Status:** Open

### Question

Should durable authoritative state ultimately use a portable Nyason snapshot, a database-backed store, another format, or a composition of portable and indexed representations?

### Decision criteria

- AI comprehension/edit reliability;
- deterministic canonicalization and fingerprints;
- atomic publication and recovery;
- schema migration and corruption isolation;
- focused-query and large-graph performance;
- practical Git/archive workflows;
- implementation and operational complexity.

### Current position

Strict JSON is the reference/debug baseline. Nyason is the actively explored Nyrwvy-native portable encoding candidate. A database is likely useful as a working/index store and may become authoritative if measured evidence supports it. Storage representation must not redefine graph semantics.

### Resolution target

Resolve only after realistic agent-workflow and storage-scale measurements make the trade-off observable. See [Nyason and storage direction](../concepts/nyason-and-storage.md).

---

## Q-002 — How should cross-repository identity, import, and collisions work?

- **Status:** Partially resolved

### Question

How should stable semantic identity behave when independently developed graphs are imported, merged, copied across repository histories, or found to contain colliding external identities?

### Decision criteria

- preserve genuine semantic continuity;
- never silently merge distinct objects because IDs happen to collide;
- make copied/new semantic objects distinguishable from preserved imports;
- keep import behavior explicit and reviewable;
- support future package/module identity without weakening typed IDs.

### Current position

Local production allocation uses typed UUIDv7 IDs. Rename, movement, clone, branch, and history-preserving fork preserve IDs. Semantic duplication and template instantiation allocate new IDs. Ordinary agents do not manufacture IDs.

**Import policy (decided 2026-09-25 for pinned dependencies):** imported declarations keep their typed identities; there is no package-qualified identity layer. When several pinned dependencies are combined, a declaration reached through more than one of them is imported once only when its content is identical (for example a shared transitive library). Any other identity collision between imports, or between an import and a local declaration, is rejected with an explicit diagnostic and never merged. General semantic merge of independently edited histories (Q-005, Q-014) remains open.

### Resolution target

Resolve before cross-repository semantic import or merge becomes a supported ordinary workflow.

---

## Q-004 — When should new structures receive first-class stable identity?

- **Status:** Partially resolved

### Question

As the semantic model grows, which new local structures or associations should become independently identified graph entities?

### Decision criteria

Give identity when independent targeting, durable diagnostics/obligations, continuity across edits, provenance, or durable query references require it. Avoid identity that only adds graph noise.

### Current position

The implemented boundary gives identity to declarations and semantic objects that require durable targeting, while association slots such as list elements remain embedded unless a concrete workflow demonstrates independent-targeting pressure.

### Resolution target

Evaluate per new construct from actual operation, query, diagnostic, and viewer requirements rather than adopting one global rule.

---

## Q-005 — How should semantic conflict, merge, and rebase work?

- **Status:** Partially resolved

### Question

What conflict model should govern multi-operation and eventually multi-agent proposals beyond deterministic ordered application to one base snapshot?

### Decision criteria

- conservative conflict detection;
- explainable rejection;
- preservation of reviewed intent and resulting diff;
- no silent reorder or rebase;
- useful treatment of provably commuting operations;
- compatibility with future semantic merge workflows.

### Current position

Operations inside a transaction run in explicit request order. A proposal is bound to its exact base and result. Stale proposals reject. Any future rebase or merge must create a new proposal and therefore a new review.

### Resolution target

Resolve incrementally when parallel proposal or semantic merge workflows become concrete.

---

## Q-006 — When does the obligation state machine need another state?

- **Status:** Partially resolved

### Question

What additional durable obligation states are required beyond the implemented `Open` and `Resolved` states?

### Decision criteria

- obligations cannot silently disappear;
- transitions are attributable;
- stale obligations remain distinguishable from resolved decisions;
- additional states must support a real query/review/repair consumer.

### Current position

`Open` and `Resolved` are sufficient today. `Invalidated` is the next justified candidate when a later semantic edit makes an old obligation no longer applicable. Do not add a larger workflow taxonomy preemptively.

### Resolution target

Add a state only when an implemented workflow needs the distinction.

---

## Q-008 — How should broader workspace views and layout behave?

- **Status:** Partially resolved

### Question

How should Nyrwvy present larger cross-function or cross-subsystem graph slices without becoming a generic giant graph renderer?

### Decision criteria

- relation-specific layout;
- stable placement across small changes;
- bounded scope and progressive disclosure;
- explicit cycle handling;
- accessibility without color-only distinctions;
- clear correspondence with semantic impact.

### Current position

Function Focus and Change Review projections are implemented and verified. Broader workspace-level views and layout algorithms remain open.

### Resolution target

Resolve from concrete navigation/review tasks and measured usability rather than selecting one universal graph layout.

---

## Q-010 — Which additional capability properties become semantic contracts?

- **Status:** Partially resolved

### Question

As filesystem, network, time, randomness, async, cancellation, or resource-sensitive capabilities are added, which properties must be represented explicitly?

### Decision criteria

Add a property only when validation, planning, execution, review, or another concrete consumer needs it. Avoid speculative metadata whose meaning no component enforces.

### Current position

The synchronous Console boundary retains its existing typed operations. Concurrent
use now consumes an explicit capability-wide host binding assertion: all worker
calls, including transitive calls and cleanup, require concurrent support before
emission. Sharing order and visibility are defined by the
[ParallelAll capability-sharing contract](../specs/parallel-capability-sharing.md).
This does not add speculative properties to semantic declarations or an ownership
system. [Program-defined capability implementations](../specs/program-capability-implementations.md)
now admit stateless, monomorphic pure functions with exact operation signatures.
They introduce no additional effects and do not remove the invocation's declared
effect. This derived restriction does not replace assertions about external host
implementations. Stateful/effectful implementations and other properties remain open.

### Resolution target

Extend capability contracts feature-by-feature when a new external behavior requires the distinction.

---

## Typed abort follow-up

- **Status:** Proposed contract, not implemented
- The [typed function-abort proposal](../specs/typed-function-abort.md) fixes the
  first boundary as local early exit from a Result-returning function. The error
  type comes from that result; cleanup and ParallelAll retain their current
  failure/termination contracts.
- Exact persisted/wire forms and lowering require implementation and the stated
  validation gates. Non-local scopes, resumable handlers and implicit propagation
  through callers are not part of this first slice.

---

## Q-012 — What ownership/resource model should the core eventually express?

- **Status:** Deferred

### Question

Should Nyrwvy remain managed-value oriented, introduce ownership/borrowing semantics, or model abstract resource-use constraints that different backends lower differently?

### Decision criteria

- AI reliability for ownership-sensitive changes;
- useful resource and concurrency safety;
- no unnecessary ceremony on managed backends;
- a viable future path for non-GC backends;
- graph/viewer understandability.

### Current position

Current semantics are managed. No backend-independent zero-cost ownership guarantee is claimed.

### Resolution target

Revisit only when a backend or resource-management use case makes ownership semantics necessary.

---

## Q-014 — What should semantic Git merge/review ultimately look like?

- **Status:** Partially resolved

### Question

How should repository storage, semantic review artifacts, and merge tooling cooperate once multiple branches can change graph state independently?

### Decision criteria

- clone/checkout reconstructs a complete program;
- semantic identity survives ordinary history operations;
- review exposes semantic consequence rather than serialization churn;
- merges do not silently destroy identity or reviewed intent;
- repository corruption remains detectable;
- no mandatory centralized service.

### Current position

Stable IDs survive clone/branch and current snapshots remain authoritative. Canonical storage and general semantic merge are still tied to Q-001 and Q-005.

### Resolution target

Resolve alongside the permanent persistence and semantic-merge workflows rather than defining Git behavior from an experimental serialization format.

---

## Q-015 — How should organizational or cryptographic approval work?

- **Status:** Partially resolved

### Question

What roles, signatures, quorum rules, or organizational policies should sit above the existing exact-proposal review binding?

### Decision criteria

- approval binds to the exact reviewed base and proposed result;
- changed or stale proposals require fresh approval;
- workflow metadata does not alter executable semantics;
- automated and human policies can coexist;
- policy can remain optional for simple/local workflows.

### Current position

Approval is graph-adjacent workflow metadata bound to the reviewed proposal/receipt; it is not executable semantic state. Cryptographic and organization-specific policy is intentionally open.

### Resolution target

Define only when a concrete multi-user governance workflow requires it.

## Resolved historical questions

The original pre-M1 question set remains in the [frozen pre-reorganization archive](../../docs-archive/2026-08-pre-reorg/open-questions.md). The following broad questions are resolved for the current architecture and no longer appear above as open blockers:

- **Q-003 — snapshot versus operation log:** the published snapshot is authoritative; history is provenance/audit. See [Milestone 1 decisions](milestone-1.md).
- **Q-007 — incremental validation:** full validation is normative; incremental validation is an optimization over the same semantics.
- **Q-009 — Typed Semantic IR versus Execution IR:** the two explicit stages are implemented and retained.
- **Q-011 — current C# representation boundary:** backend mapping is downstream and constrained by semantic contracts; see the [C# backend spec](../specs/csharp-backend.md).
- **Q-013 — RAST provenance:** no special provenance framework; see [RAST provenance](rast-provenance.md).
