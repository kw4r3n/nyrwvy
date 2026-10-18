# General-purpose programming: language and tool implementation plan

**Status:** Completed (2026-09-25) for the G1-G4/T1-T3 completion criteria. The follow-up work order of 2026-09-26 below is also complete.

**Updated:** 2026-10-18

**Scope:** General-purpose language features, external-integration contracts, and tools for creating and maintaining the semantic graph

## Purpose and boundaries

Nyrwvy is the layer whose program is a persistent typed semantic graph, and which builds and changes programs without editing source text directly. Generated code is a projection and has no authority over semantics.

rrawrlet, a separate Python application by the same author (an OpenCode-based agent-team tool, not published with this repository), is a subject for discovering gaps; this plan does not add product-specific concepts such as Leader, Member, attempt or Apply to the core. External libraries may be used, but language gaps must not be hidden by moving the application's decisions and control as a whole into host functions. Asynchronous processing and execution control are also expressed through general-purpose features.

This document covers the current state and the next work. The limits at the start of the plan, the PR states of that time, the per-stage implementation records and the investigation evidence were moved to the [history](../../docs-archive/2026-09-general-purpose-programming/roadmap.md). Something marked "not implemented" in the history is not a current remaining item.

## Current coverage

"Complete" means that each item's bounded completion criteria are met, not that every feature of a general-purpose language exists. For exact contracts, see the [language subset](../reference/language-subset.md) and the linked specifications.

| ID | Goal | Current coverage | Remaining boundary |
| --- | --- | --- | --- |
| G1 | Aggregation and runtime keyed lookup through ordinary authoring | `ListFold`, callbacks with context, composition of first-match search and item access, String equality, concatenation and Int64 conversion, `Int64FloorDivide`/`Int64FloorModulo` with the same floor division and modulo as Python (they do not fail even for a divisor of 0), `StringLength` counted in code points and a range-clamping `StringSlice`, `ListItemAt` returning `None` out of range, structural equality `ValueEqual` for [equatable types](../reference/language-subset.md#structured-values-and-control), code-point String ordering `StringLessThan`, stable sorting with a pure comparison callback `ListSortBy`, and `BoolNot` and `BoolAnd`/`BoolOr`, which evaluate the right operand only when needed. `ListEffectFold` supports ordered effects and stopping at the first Err. Immutable insertion-ordered [`Map<K, V>`](../decisions/map.md) and [`Set<T>`](../decisions/set.md) support equatable keys/items, keyed lookup and updates, Map callbacks/filtering and set algebra (post-M2 orders 44–48). | [Map and Set equality](../decisions/map-set-equality.md) was added on 2026-10-18. Constrained generic keys/items and a direct Set fold remain outside the maintained subset; `SetToList` with `ListFold` covers folding today. Early exit from a pure fold itself and further String operations are added when they are [warranted](post-m2-roadmap.md#selection-rule-from-here), whether or not a task has needed them yet. |
| G2 | Reuse of context, types and predicates without duplicating bodies | Function-typed parameters, explicit context arguments, [pure capture-free function values](../specs/pure-function-values.md), [Product/Union with type parameters](../decisions/generic-types.md) (2026-10-18). | General closures, constrained type parameters, effectful function values. |
| G3 | Connecting external implementations in another module with typed values and failures | Explicit C# `ExternalImplementation`, the shared runner's `hostSources`/`hostCapability`, diagnostics for composite values, `Result` and host exceptions. [Pure in-program implementations](../specs/program-capability-implementations.md). | In-program Capability implementations with state or effects. `FailureType` is explicitly unsupported by the C# backend. |
| G4 | Structured concurrency and cleanup | `UseResource`, `ParallelAll`, cooperative cancellation, waiting for every worker to finish, the [parallel Capability sharing contract](../specs/parallel-capability-sharing.md), starting tasks, waiting (Any/All) and cancelling individual tasks within a [task scope](../decisions/concurrency-control.md) (phase 1), holding `Task<T>` in fields of declared types (not `TaskScope`, and transitively not across a Capability boundary or in `runPure` values), and deterministic wait time limits on `runPure`'s virtual clock (phase 2). | Declared concurrency safety (Q-010) is revisited only if a need is shown. No custom scheduler, ownership or borrowing has been introduced. |
| T1 | Declaration changes and deletion with review | Adding and deleting Product fields and migrating their types, migrating argument/return types, adding variants with fields, deleting unused functions, types, Effects and Capabilities/Operations, and [migrating a Capability operation's argument and result types](../decisions/capability-operation-signature-migration.md) with its implementations and callers (2026-10-07). | Changing an operation's parameter shape, effects or `FailureType`, Union variant field type migration, automatic rewriting, a general-purpose migration DSL. |
| T2 | Reproducing multiple and multi-level dependencies and updating them explicitly | Content-retaining sidecars, sharing the same ID only for identical content, diamond dependencies, update previews and application bound to a reviewed fingerprint. | Publishing an update together with a local migration, a registry, online distribution, semantic merge. |
| T3 | A shared path for running, testing and diagnostics | Typed input/output, expected values and fake/host Capability execution through `runPure`, diagnostics with semantic IDs, ordinary tools/MCP. [Opt-in `verifyChange`](../specs/agent-authoring-session.md#opt-in-change-verification) compares a pending structural proposal with the committed baseline on chosen cases through the same path (2026-10-06); agreement on tested cases is not proof. Float64 input/output (JSON numbers in shortest round-trip form and `"NaN"`/`"Infinity"`/`"-Infinity"`/`"-0"`, bitwise matching that treats every NaN as equal) and the `Int64ToFloat64` conversion were [added at probe 3's request](../decisions/float64-conversion-and-run-values.md). | Operations with a declared `failureType` remain unsupported. Float64 to Int64 conversion ([`Float64RoundToInt64`/`Float64FloorToInt64`](../decisions/float64-to-int64.md)) and decimal rounding ([`Float64Round`](../decisions/rounding-half-away-from-zero.md)) were added after this plan completed. Input/output and diagnostics needed by new semantics are added one at a time. A full debugger is not assumed. |

The stage of re-implementing the starting problems of G1-G4/T1-T3 is finished. [Effect requirements derived from bodies](../specs/body-effect-requirements.md) are also available in inspect/review. Derived information does not change declarations automatically.

## Next work order

The order chosen on 2026-09-26 is as follows.

| Order | Work | Minimal scope and completion criteria |
| --- | --- | --- |
| 1 | Organize the current state and the history | Reorganize the body into the current coverage table and the work order, and keep old limits, investigations and implementation records in the archive. Completed by this reorganization. |
| 2 | Choose Capability implementations inside Nyrwvy (implemented) | Map each operation to an existing pure, capture-free function, create a Capability value and pass it. Pass two implementations to the same consuming function to switch the result, and go through ordinary authoring, review, save/reopen and the shared runner. Reject missing operations, type mismatches and impure implementations before publication. [Execution and verification record](../verification/program-capability-implementations-20260926.md). |
| 3 | Specify and implement abort semantics (complete) | [Typed function abort](../specs/typed-function-abort.md) defines local early exit from a `Result<T,E>` function. It states release, failure precedence, and cancellation of and waiting for concurrent workers. [Implementation and verification are also complete](../verification/typed-function-abort-20260926.md). Ordinary authoring, save and reopen, C# execution, and the failure contract for release and concurrent workers were confirmed. |

A Capability implementation does not automatically narrow the caller's existing Effect contract. The general form in which an implementation introduces new effects, state and capture, and handlers that resume a continuation are not part of this first stage. Nor does it mean that [Semantic independence](../concepts/semantic-independence.md) has been adopted as a whole.

The implementation queue based on the rrawrlet slice findings is maintained in the [post-M2 roadmap](post-m2-roadmap.md#language-and-tool-implementation-record).

These three work items and the abort implementation are complete. A [practical example built only with the ordinary tools](../../examples/function-abort/README.md) was also added. The next feature extensions are chosen by building real processing through this path and picking what is confirmed to be missing, or by the owner choosing an operation that is [warranted](post-m2-roadmap.md#selection-rule-from-here) without a consumer yet. A missing operation that is not warranted is not added.

## Conditions every work item maintains

- State the target processing, the current limit, the minimal change, the invariants of semantics, identity and review, and the completion criteria.
- Check whether existing features or libraries can express it, and do not add more core nodes or shared infrastructure than needed.
- Make the create, inspect and change paths available from the shared tool layer/MCP. Adding a top-level tool per feature is not required.
- The system assigns new IDs, and existing IDs are preserved. Do not publish unrepaired references or type errors.
- At the start of a work item, check the target branch and related PRs, and do not duplicate completed or prepared work.

## Verification and completion judgment

Within the implemented scope, go through "authoring → type and effect validation → review/commit → save and reopen → lowering/backend → execution results and diagnostics". For changes to the storage format or protocol, state how compatibility and the schema version are handled.

Following the [Verification philosophy](../developer/verification.md), put important contracts and regressions in fast, and representative generation and execution checks in extended. Reuse existing live authoring success records and do not repeat the same model evaluation. Add a verification of a boundary that can only be confirmed in a real environment only when that boundary changes.

## Conditional and out-of-scope work

Ownership, runtime state identity, a custom scheduler, per-declaration hashes, storage changes and semantic merge are reconsidered starting from a concrete requirement or measurement. Structural changes for build time also follow the [post-M2 conditions](post-m2-roadmap.md#selection-rule-from-here) and are not a prerequisite for adding features.

Concrete adapters such as Codex/OpenCode/Git, and rrawrlet's UI, operating policy and execution data persistence are the work of the application or libraries.

## References

- [Vision](../concepts/vision.md) / [Architecture](../developer/architecture.md)
- [Expression authoring](../specs/expression-authoring.md) / [AuthoringSession and dependency management](../specs/agent-authoring-session.md)
- [C# backend](../specs/csharp-backend.md) / [Failure policy](../specs/failure-obligation-policy.md)
- [Open questions](../decisions/open-questions.md) / [History of the plan and implementation](../../docs-archive/2026-09-general-purpose-programming/roadmap.md)
