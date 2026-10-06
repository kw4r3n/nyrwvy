# Capability operation signature migration

- **Status:** Accepted
- **Date:** 2026-10-07
- **Roadmap:** [post-M2 owner choices of 2026-10-06 and 2026-10-07](../plans/post-m2-roadmap.md#owner-choices-of-2026-10-06-and-2026-10-07), order 57; the T1 row of the [general-purpose programming roadmap](../plans/general-purpose-programming-roadmap.md#current-coverage)

T1 listed Capability signature migration as a remaining boundary: an operation's argument or result type could not change once functions invoked or implemented it. The [config-request batch example](../../examples/config-request-batch/README.md) needed to distinguish retryable from permanent fetch failures, which requires changing `fetch` from `Result<String,String>` to `Result<String,FetchFailure>`.

## Decision

`simulateCapabilityOperationSignatureMigration` (and `…ByName`) migrates one existing operation's argument types and result type as one ordinary structural proposal. Capability, operation, parameter and function IDs are retained. Parameter count, order and names, effects and `FailureType` stay unchanged; expected failures continue to use `Result`. This is bounded type evolution, not arbitrary declaration editing.

The request supplies the complete operation parameter type map, the result type and the complete set of affected function repairs. A repair supplies the existing function's parameter type map, result type and replacement recipe, so a constructed implementation and its callers migrate together. The affected set is derived from actual uses: nested `CapabilityInvoke` nodes, `CapabilityConstruct` mappings and every semantic reference to an affected function whose signature changes (recursively including changed callers). Unrelated signatures cannot seed it. The request must cover exactly this set; duplicates, unknown parameter IDs and missing or unrelated repairs reject. All repaired signatures are installed before any recipe is materialized, and the ordinary full-candidate validator checks the completed graph before registration and publication. Structural receipts and stale-commit rejection are reused. Imported declarations remain read-only. The exact request and rejection codes are in the [session contract](../specs/agent-authoring-session.md#capability-operation-signature-migration).

## Reason

Repairing implementations and callers in the same publication keeps every published snapshot type-correct, as the parameter-type, result-type and Product-field migrations already do. Requiring the full repair set, rather than rewriting automatically, keeps each body change reviewable.

## Consequences

- The batch example declares `FetchFailure = Retryable(String) | Permanent(String)`, migrates `fetch` and retries only `Retryable` up to the configured limit. Host and fake implementations adopt the changed boundary explicitly.
- Graph publication does not deploy a host atomically: a C# host implementation must be updated alongside the published graph.
- Historical old/new response queues need explicit `runPure` expectations; `verifyChange` is appropriate only for entries whose values are unchanged and compatible across the migration.
- No migration DSL and no new runtime failure semantics are introduced.

## Revisit conditions

- A change to an operation's parameter count, order, names, effects or `FailureType` is needed by a concrete program.
- Repair sets become large enough in a real program that writing every repair by hand is the dominant cost.
