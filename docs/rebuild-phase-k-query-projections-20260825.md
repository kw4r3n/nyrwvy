# F# Milestone 0 rebuild — Phase K semantic query projections — 2026-08-25

## Status

**Verified through a clean local compiler/test boundary.**

The exact verified code head was:

```text
99264255c99b5827c7adc83f545950c45940f4b1
```

Verification environment and result:

```text
.NET SDK: 10.0.111
clean:     bin/ and obj/ removed before restore
restore:   success
build:     success, 0 warnings, 0 errors
full test: 73 passed, 0 failed, 0 skipped
ExecutionIrTests:       6 passed
ExecutionIrCodecTests:  3 passed
CSharpBackendTests:     4 passed
SemanticQueryTests:     7 passed
ChangeReviewQueryTests: 3 passed
```

The uploaded TRX bundle reports `executed = total` for every focused suite and zero `failed`, `error`, `aborted`, or `notExecuted` results.

GitHub Actions capacity remained unavailable, so the clean local run and TRX bundle are the accepted Phase K verification evidence.

## Purpose

Phase K makes semantic information directly queryable without requiring an AI agent or viewer to reconstruct common facts from the whole snapshot.

These APIs are **read-only projections**. They do not create a second authoritative graph or cache semantic facts as mutable state.

## Focus query

`SemanticQuery.focusFunction` returns one focused payload containing:

- snapshot identity;
- the authoritative focused `SemanticFunction` body;
- derived type for every graph node when derivable;
- direct callers and callees;
- related function signatures;
- open obligations targeting nodes owned by the function;
- referenced type declarations;
- referenced capability declarations;
- referenced effect declarations;
- explicit missing declaration IDs when references cannot be resolved.

The function itself remains authoritative. The focus view is disposable and reproducible from the snapshot.

## Local declaration closure

A focused payload should remain understandable without forcing the consumer to repeatedly fetch obvious declaration dependencies.

Phase K therefore follows referenced declarations transitively:

```text
function/node TypeRef
    -> named type
        -> product/variant fields
            -> their TypeRefs
    -> capability
        -> operation parameters/result/failure
        -> operation effect
```

Traversal is ID-based and stops on already-visited declarations, so recursive type graphs do not recurse forever.

Missing declarations are not invented. They are surfaced explicitly as:

- `MissingFunctionIds`
- `MissingTypeIds`
- `MissingCapabilityIds`
- `MissingEffectIds`

This lets an AI distinguish “not included yet” or “broken reference” from a valid declaration instead of guessing a fallback meaning.

## Relationship queries

Phase K adds deterministic queries for:

- `getFunction`
- `getType`
- `getCallers`
- `getCallees`
- `getDependencies`
- `getUses`
- `getOpenObligations`
- `focusFunction`

Caller/callee information is derived only from authoritative `DirectCall` nodes. No parallel call index is stored.

Direct value dependencies are centralized in `SemanticFacts.directValueDependencies` so validator-adjacent reasoning and query reasoning do not evolve separate definitions of node references.

## Incomplete snapshots remain inspectable

A blocking obligation prevents Typed Semantic IR projection and execution lowering, but it must not prevent an AI or human from understanding the proposed state.

The focused query therefore remains available on an incomplete snapshot and exposes its open obligations.

This separation is intentional:

```text
Incomplete semantic snapshot
    -> query/review: allowed
    -> execution lowering: blocked
```

## Change Review projection

`ChangeReviewQuery.project` consumes the existing `TransactionSimulation` and produces a review payload containing:

- transaction ID;
- base and proposed snapshot IDs;
- the existing authoritative `SemanticDiff` value;
- semantic readiness;
- affected function IDs;
- affected type IDs;
- focused function payloads for affected functions.

The review layer does not calculate a competing diff. `SemanticDiff` remains the sole meaning-level change record.

### Rename

A function rename focuses the same stable `FunctionId`, preserves the rename diff, and remains semantically complete.

### Signature change with missing argument

Adding a parameter with `LeaveMissing` identifies both:

- the changed callee signature; and
- the caller that owns the obligated call node.

The caller focus exposes the blocking obligation and Change Review reports incomplete readiness.

### Obligation resolution

Resolving the missing argument focuses the caller whose call edge changed and restores semantic readiness when no other blockers remain.

## AI-first consequences

Phase K deliberately prefers explicit query records over broad graph traversal instructions.

An AI can ask for a focused function and receive:

- what the function is;
- what its nodes mean and type to, when derivable;
- who calls it and what it calls;
- which declarations are needed to understand the slice;
- what is unresolved;
- what changed during a transaction.

It does not need to infer these relationships from display names or serialization order.

## Next gate

The next phase is a read-only Focus / Change Review viewer that consumes these query projections.

The viewer must remain a projection client. It must not own semantic state, parse generated C#, or create a parallel graph representation.
