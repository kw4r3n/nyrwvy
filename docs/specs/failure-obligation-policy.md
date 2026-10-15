# Failure, diagnostic, obligation, and runtime-result policy — 2026-08-26

## Status

Accepted semantic classification policy for Milestone 1.

Nyrwvy distinguishes four different concepts that conventional languages often collapse into exceptions or error strings:

1. invalid proposed program change;
2. valid but unresolved program state;
3. unsupported lowering/backend configuration;
4. expected runtime-domain failure represented by the program itself.

These categories must not be interchanged merely because one implementation mechanism is convenient.

## Classification rule

Use the following decision order when designing a new feature or failure case.

### A. Is the proposed semantic state structurally or semantically invalid with no safe unresolved representation?

**Use transaction rejection / validation diagnostic.**

Examples:

- dangling stable reference;
- wrong argument type;
- duplicate stable ID;
- `ListMap` mapper has incompatible signature;
- effectful mapper supplied to a pure-only M1 `ListMap`;
- malformed Float64 literal bit payload;
- unsupported builtin generic arity.

The proposed authoritative state does not publish.

### B. Is the graph structurally valid, but a real semantic decision is still missing and can be represented explicitly?

**Use a typed obligation.**

Examples:

- new parameter exists but one call site has no chosen argument;
- future union variant addition leaves an intentionally unresolved match repair;
- a migration reaches a genuinely ambiguous but safely incomplete decision point.

The state may publish only under explicit incomplete-publication policy and cannot pass the obligation's blocking stage.

### C. Is the semantic program valid, but the selected execution/backend profile cannot currently represent or bind it?

**Use lowering/backend readiness diagnostic unless there is a concrete durable workflow that benefits from a backend-specific obligation.**

Examples:

- missing C# capability binding;
- backend does not support a new Execution IR instruction yet;
- target runtime lacks a required feature.

Do not mutate the semantic graph merely to record a backend implementation gap.

A backend-specific obligation may be introduced later only if it has a durable user/agent resolution workflow and explicit blocking stage.

### D. Is the failure an expected result of executing a valid program with valid runtime data?

**Represent it in program semantics, usually with `Result`, `Option`, or another explicit data type.**

Examples:

- parsing user text can fail;
- lookup may have no value;
- an explicitly checked numeric conversion can fail for NaN/out-of-range input;
- a domain validation function returns `ValidationError`.

Do not turn normal domain failure into transaction diagnostics or hidden host exceptions.

## Diagnostics versus obligations

### Diagnostic

A diagnostic explains why the requested stage cannot proceed.

Properties:

- derived from current request/snapshot/backend state;
- machine-readable stable code;
- explicit stage/category;
- affected stable targets when available;
- deterministic ordering where multiple diagnostics are returned;
- human message as presentation, not classification authority.

A diagnostic does not need durable identity unless a future workflow demonstrates that it must survive unrelated edits.

### Obligation

An obligation is durable unresolved semantic work inside a valid snapshot.

Properties:

- stable identity;
- explicit kind;
- affected semantic target(s);
- origin transaction/provenance;
- blocking stage;
- explicit state (`Open`, `Resolved`, later `Invalidated` when needed);
- resolution through semantic operation rather than string acknowledgement.

An obligation is not a stored compiler error.

### Retiring a resolved obligation with its function

`DeleteFunction` retires resolved obligations whose call nodes belong to the
removed function body. Any open obligation on that body still rejects the entire
transaction with `FunctionHasObligations`; resolving it is an explicit semantic
choice, including when resolution precedes deletion in the same ordered batch.
References from other functions still block deletion as before.

Retirement is part of the reviewed function deletion, never an invisible cleanup
or a standalone way to discard a live body's obligations. The `FunctionRemoved`
diff retains every retired `MissingArgumentObligation` exactly, including its
origin, call, parameter and resolution transaction/value; wire and HTML reviews
show those records. Only the removed body's resolved records leave the proposed
snapshot. Other obligations remain exact. Rejection publishes none of the
candidate changes, and reviewed commit retains the normal exact-request,
stale-base and blocking-obligation policy checks.

The immutable base snapshot remains available through workspace history; the
current snapshot no longer refers to deleted nodes. This does not add a durable
history store or promise historical snapshots across file replacement/restart.
No snapshot schema, fingerprint domain or `ObligationStatus` state changes.
Whole-body/target replacement still refuses bodies targeted by obligations;
general invalidation or retirement while an owner remains alive is not added.

## Public diagnostic shape target

The current implementation has intentionally small diagnostic records. As M1 grows, public cross-component diagnostics should converge toward a typed shape conceptually equivalent to:

```text
Diagnostic {
    code
    stage
    severity
    targets[]
    transactionId?
    operationIndex?/operationKind?
    message
    repairHints[]?
}
```

This is a target contract, not a requirement to add unused fields immediately.

Rules:

- `code`, not `message`, is the machine classification key;
- target domains are typed rather than one ambiguous string ID;
- exception stack traces are internal debug data, not public diagnostic semantics;
- repair hints are proposals, not automatically accepted decisions.

## Stage taxonomy

Keep stages explicit enough that an agent can determine what remains possible.

Initial conceptual stages:

- `GraphIntegrity`;
- `SemanticValidation`;
- `TransactionSimulation`;
- `PublicationReview`;
- `TypedSemanticLowering`;
- `ExecutionLowering`;
- `BackendBinding`;
- `BackendEmission`;
- `ExternalTooling`.

The exact enum may be smaller initially. Do not encode stage only into free-form diagnostic codes/messages if multiple consumers need it.

## Severity

Severity is separate from blocking behavior.

Initial categories may be:

- `Error` — stage cannot proceed;
- `Warning` — stage may proceed but review should surface the condition;
- `Info` — explanatory/non-blocking state.

Do not introduce warnings until a concrete non-blocking condition exists. Nyrwvy must not create a warning system merely to copy conventional compilers.

## Deterministic diagnostic ordering

When multiple diagnostics are returned, ordering is presentation/determinism infrastructure rather than semantic execution order.

Use stable explicit ordering such as:

1. stage/category;
2. stable target IDs;
3. diagnostic code;
4. stable operation position when applicable.

Never imply semantic priority from incidental traversal/hash-map order.

## Runtime numeric implications

### Int64 wrapping arithmetic

`Int64WrappingAdd/Subtract/Multiply` are total semantic operations. Overflow is defined behavior, not a runtime error and not a diagnostic.

### Future checked Int64 arithmetic

If checked arithmetic is introduced, choose one explicit contract:

- result-returning total node, e.g. `Result<Int64, ArithmeticError>`; or
- a distinct semantic trap/effect model if the language later introduces such a concept.

Do not inherit C# `checked` exception behavior implicitly.

### Float64 arithmetic

IEEE binary64 NaN/infinity/signed-zero results are normal values under the Float64 contract, not diagnostics.

### Float64 -> Int64 conversion

A future conversion that can encounter NaN, infinity, fraction, or out-of-range values must expose its rounding/failure semantics explicitly. A likely design is a `Result`-returning conversion with an explicit rounding policy.

## Collection implications

For M1 pure specialized list operations:

- wrong source type -> validation diagnostic;
- wrong mapper/predicate signature -> validation diagnostic;
- effectful mapper/predicate -> validation diagnostic because effectful traversal is outside the node contract;
- empty list for `ListAny`/`ListAll` follows explicitly documented total Boolean semantics, not an error;
- list element runtime domain failures belong to the mapper/predicate's own return type, not to hidden List machinery.

Effectful traversal is the separate construct `ListEffectFold`, leaving pure `ListMap` semantics unchanged. Its sequencing is left to right, each item exactly once. Expected failure is the folder's own `Result`: the first `Err` stops the traversal and is the node's value, a runtime-domain failure rather than a host exception. Cancellation is not part of this construct (see G4).

Structured concurrency (`ParallelAll`) keeps expected failures in the workers' `Result`: the lowest-index `Err` is the node's value, independent of timing, and it requests cooperative cancellation of the other workers, which the node always awaits. A worker host exception also cancels the others and is rethrown after all of them finish; it is never converted into a program `Result`.

Scoped resource release (`UseResource`) keeps expected failures in `Result`: acquisition `Err` short-circuits, a body `Err` takes precedence over a release `Err`, and a release `Err` surfaces only after a successful body. Host exceptions remain host failures: release is attempted before the original exception propagates, and a second host failure during that release is suppressed so it cannot mask the first.

## Backend errors are not semantic type errors

If a semantically valid snapshot lowers to backend-neutral Execution IR but a backend does not yet support it, report backend readiness/emission failure.

Do not reject the semantic type merely because the first backend is incomplete.

Likewise, do not add semantic fields whose only purpose is to satisfy one backend's object model.

## Tool/protocol failures

Malformed API calls, unavailable files, process-launch failures, and connector errors belong to tooling/infrastructure diagnostics.

They should be distinguishable from semantic-model failures in agent evaluation.

Example:

```text
wrong tool argument shape != invalid semantic operation
```

This distinction is required by the M1 agent evaluation plan.

## No catch-and-convert rule

Do not broadly catch internal exceptions and convert them into generic semantic diagnostics.

Internal exceptions indicate either:

- implementation defect;
- invariant violation;
- unexpected external infrastructure failure.

Public boundaries may translate known expected implementation errors into typed diagnostics, but unknown exceptions should remain visibly distinct during development so they are not mistaken for valid user/program failure modes.

## Review checklist for a new failure case

Before adding an error path, answer:

1. Is this invalid program structure, unresolved semantic work, backend readiness, runtime-domain failure, or tooling failure?
2. Can a safe incomplete snapshot represent it without guessing?
3. Does it require durable identity/provenance, or is a derived diagnostic sufficient?
4. Is the failure expected at runtime and therefore part of the program's type?
5. Am I accidentally inheriting exception/default behavior from the C# backend?
6. Can an agent distinguish and repair the condition from structured data without parsing prose?

If these answers are unclear, the feature contract is not ready to implement.

## Typed function abort

The implemented [typed function abort](typed-function-abort.md) preserves this
classification: an explicitly typed, local early exit delivers an ordinary Err
at the function boundary. Host faults and backend readiness errors remain
separate. ResultErr remains ordinary data that can be matched within the same
invocation; Abort skips the invocation's remaining ordinary operations.
