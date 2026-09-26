# Semantic independence

**Status:** Proposed

This document states how Nyrwvy keeps its own meaning separate from the backends that realize it. It collects terminology, the observable behavior each concept must preserve, what a backend may choose freely, and the gaps that remain. It is a direction for review, not an accepted specification; where it describes current behavior, the linked specs and [language subset](../reference/language-subset.md) remain authoritative.

## Goal

Preserve every observable semantic distinction, and delegate an implementation choice to a backend only when every allowed choice has the same meaning. Backend independence does not mean inventing new terminology or avoiding familiar constructs such as functions, `Result`, `Bind`, or `match`. It means Nyrwvy defines the meaning itself, and a backend never supplies missing meaning.

Backend independence and expressive breadth are separate axes. The main risk to the second is needing a new core node for every new reusable computation. A closed node enumeration is acceptable; a closed enumeration that is the only way to add a computation is not.

## Three responsibilities

| Responsibility | Covers | Current home |
| --- | --- | --- |
| Change management | identity, snapshots, proposals, review, publication | [architecture](../developer/architecture.md) invariants 1-6 |
| Program meaning | values, computations, resources, contracts, scopes, dependencies, interpretation | Typed Semantic IR, [expression authoring](../specs/expression-authoring.md), [failure policy](../specs/failure-obligation-policy.md) |
| Realization | lowering, runtime, backend binding | Execution IR, [C# backend](../specs/csharp-backend.md) |

This is a split of responsibility, not a package hierarchy.

## Terminology

| Term | Meaning in Nyrwvy | Not to be confused with |
| --- | --- | --- |
| Entity | A tracked semantic object identified by a stable ID. | Its name or its content. |
| Revision | The state of an entity within one immutable snapshot: the pair `(SnapshotId, StableId)`. | An independent per-entity version counter, which does not exist. |
| Content hash | A fingerprint of content under explicit normalization and dependency rules. Currently `SnapshotFingerprint` and `SemanticFingerprint` cover whole snapshots only. | A proof that two implementations behave identically. |
| Display name | Presentation metadata. Tools may accept a name as a lookup key, but resolve it to an ID before any operation, and reject an ambiguous name. | Identity. |
| Edit operation | A typed change to the semantic graph, reviewed and published as a transaction. | Capability operation. |
| Capability operation | A typed runtime request (parameters, result, failure type, effect) invoked on a capability value. | Edit operation. |
| Effect | A declared, nominal description of actions a function may perform. It has no operations of its own. | A set of operation names. |
| Capability | A value that grants access to a resource, authority, or implementation, and carries its operations. | The implementation behind it. |
| Handler | Interpretation of runtime requests within a scope. Program-defined capability values provide bounded implementation selection; resumable handlers remain absent. | Backend binding. |
| Backend binding | The realization of capability operations chosen by a backend profile, by `CapabilityOperationId`. | Program meaning. |

Choosing an interpretation composes program meaning; choosing a backend only realizes it. A fixed clock and a system clock are different interpretations, not equivalent realizations.

## Observable behavior and backend freedom

| Concept | Must be preserved | A backend may choose |
| --- | --- | --- |
| Identity | Resolution is `(snapshot, entity)`. Names never decide identity. | Storage layout and indexes. |
| Value update vs shared mutation | Building an updated value leaves other holders with the old value. Changing shared state is possible only through a capability operation, so its effect is declared and visible. | Copying or structural sharing of values. |
| Abort vs fallback | Fallback (`match` on `Result`/`Option`) continues with another value. An abort exits its owning invocation; that invocation never resumes. | How an abort propagates, such as `Result` threading or an internal exception. |
| Declared vs inferred requirements | A function's declared `RequiredEffects` is its public contract. Requirements computed from its body must be a subset; widening the contract requires an explicit reviewed edit. | How the check is implemented. |
| `Bind` and ordering | Its input is evaluated once, before its body. Independent effectful siblings stay an `AmbiguousEffectOrder` error rather than taking a backend order. | Evaluation strategy that keeps this order. |
| `ParallelAll` | Waits for every worker. The first `Err` requests cancellation; the result is the `Err` of the lowest failing index, or all values in source order. | Scheduling and degree of parallelism. |
| `UseResource` | Release always runs after a successful acquisition, on every exit. | How release is wired, such as `try/finally`. |
| Numeric domains | `Int64` wraps; `Float64` is IEEE 754 binary64. | Instruction selection with the same results. |
| Unsupported feature | Reported as a backend-readiness diagnostic, never as a semantic error or a silently changed program. | Which features it supports. |

## Dedicated constructs and expansion

A dedicated semantic construct is justified when it improves validation, editing, or review. A new construct needs type, scope, effect, and execution contracts, and either a checkable expansion into existing meaning or an explicit trust boundary. High-level structure stays authoritative; loops or state machines produced by lowering never replace it.

Current dedicated constructs and their status:

| Construct | Expansion into existing meaning | Why it is dedicated |
| --- | --- | --- |
| `ListMap`, `ListFilter`, `ListAny`, `ListAll` | Expressible with `ListFold`. | Review reads the intent directly. |
| `ListEffectFold` | Not expressible; pure callbacks cannot perform effects. | Ordered effectful traversal. |
| `UseResource` | Not expressible; host exceptions are outside program meaning. | Release on every exit. |
| `ParallelAll` | Not expressible; there is no other concurrency construct. | Structured concurrency and cancellation. |

An `ExternalImplementation` binding is a trust boundary: the host asserts that its implementation conforms to the generated interface, and Nyrwvy cannot derive that fact. Review should keep asserted conformance separate from derived facts.

Nyrwvy does not replace typed meaning with an opaque `Operation(name, payload)` and does not introduce a plugin or dialect framework.

## Current gaps

Each gap is classified as missing semantics, an authoring limitation, or unsupported by the backend.

| Gap | Class | Smallest change | Open decisions |
| --- | --- | --- | --- |
| Broader scoped interpretation: stateful/effectful implementations and resumable handlers are absent. Pure stateless program implementations are now available. | Missing semantics | The bounded pure case is covered by [program capability implementations](../specs/program-capability-implementations.md). Extend only for a concrete consumer. | Whether an implementation may introduce effects the caller must declare. Resumable handlers and continuations stay out of scope. |
| Function-local abort is implemented; propagation through callers still needs explicit matching. | Implemented bounded control flow | [Typed function abort](../specs/typed-function-abort.md) implements local early exit as Err from a Result-returning function, with cleanup and the existing worker-failure contract. | Exact authoring, versioned persistence and explicit IR termination are implemented; broader non-local abort remains outside this contract. |
| Runtime state identity is not distinguished from definition identity. | Missing semantics, deferred | None until a use case needs it. | Relation to [Q-012](../decisions/open-questions.md). |
| No per-declaration content hash. | Not a gap yet | Add one only when dependency upgrade or semantic diff needs "unchanged" per declaration. | Normalization and dependency rules. |

Projections, aliasing analysis, and a borrow system are deferred until concrete semantics require them. Expressing fail, await, yield, or retry as operations is a candidate, not a design: renaming a construct does not define suspension, resumption, cancellation, failure propagation, child lifetime, or cleanup.

## Design probes

These examples test the direction; they are not an implementation mandate.

| Probe | Current result |
| --- | --- |
| Value update vs shared mutation | Distinguishable: pure construction versus a capability operation with a declared effect. |
| Abort vs fallback | Fallback and typed function-local abort are implemented; recovery belongs to the caller. |
| Generic list search | `find<T>(list, predicate)` composes `ListFold`, callback context, and pure capture-free function values. The same body accepts different predicates and element types. |
| Alternate request interpretation | Pure stateless implementations can be chosen and passed by the program; host binding remains available. |

## Acceptance question

The function-value gap is covered by [pure function values](../specs/pure-function-values.md), with monomorphic named references and generic consumers. Capturing closures and effectful function values remain outside this slice.

The effect-reporting gap is covered by [body effect requirements](../specs/body-effect-requirements.md): inspect and review show the derived requirements and their semantic evidence separately from the declared contract.

The former shared-capability gap is now covered by the accepted
[ParallelAll sharing contract](../specs/parallel-capability-sharing.md): no
cross-worker order is promised; host bindings explicitly assert concurrent safety
and visibility, and emission checks transitive worker operations. This resolution
does not promote the other proposals in this document to accepted specifications.

Can a new computation be defined by composing existing meanings, stay inspectable, editable, and reviewable at that level, and report backend non-support without changing its meaning? All three hold for the bounded pure implementation slice. Stateful/effectful interpretation remains an open extension.

## Out of scope

Broad refactoring, a second backend, semantic merge, a generic extension framework, and full continuation or borrow systems.

Related: [Vision](vision.md), [Architecture](../developer/architecture.md), [Open questions](../decisions/open-questions.md), [General-purpose programming roadmap](../plans/general-purpose-programming-roadmap.md).
