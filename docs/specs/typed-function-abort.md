# Typed function abort

**Status:** Implemented (2026-09-26)

Typed function-local abort is available through ordinary exact-ID and symbolic
recipes, reviewed publication, snapshot reopen, and the common C# runner.
[Verification and implementation evidence](../verification/typed-function-abort-20260926.md)
and a [runnable ordinary-tool example](../../examples/function-abort/README.md)
cover the boundaries below.

## Chosen boundary

An abort terminates the **current semantic function invocation** with a typed
error. The owning function must declare `Result<T, E>` as its result type, and the
abort payload must have type `E` exactly. The caller receives `Err(payload)`.
There is no process termination, dynamically selected handler, non-local escape
through callers, or continuation to resume.

The boundary is the nearest owning `SemanticFunction`, not the innermost Bind,
match, branch, resource scope, or parallel join. Nested ValueGraphs belong to
that same function; directly called functions and callbacks have their own
boundaries. The result type fixes the error contract and makes it visible to
ordinary callers without a new effect annotation.

This bounded form is chosen because it enables an early exit from deeply nested
Bind/match bodies while composing with the existing function, resource and worker
Result contracts. A broader computation-scoped abort would need an explicit
scope/handler and effect contract; it is not silently supplied by host exceptions.

## Observable behavior

`Abort(error)` denotes the semantic node. Both recipe protocols encode it as
`{"kind":"abort","error":<recipe>}`; the payload must produce the owning function
result's error type. No result type or target function ID is supplied in a recipe.

1. Evaluate the error payload once under existing evaluation-order rules.
2. If that evaluation completes normally, terminate the owning invocation with
   `Err(error)`. Do not evaluate its remaining Bind body, enclosing branch/match
   continuation, or any later ordinary operation in that invocation.
3. Complete cleanup for scopes actually entered by that invocation before delivering
   its outcome. A resource scope owned by its caller performs its own cleanup
   after receiving the Result, as specified below.
4. A caller may explicitly match the returned Result and recover. That is a new
   continuation in the caller, never resumption of the aborted invocation.

`ResultErr` remains ordinary data: the same invocation can store, inspect or
match it and continue. `Abort` is abrupt control flow and cannot be caught by a
Result match inside its owning invocation. Cancellation is a request observed
cooperatively, not an abort payload or forced termination. Host faults remain
runtime faults and are not converted into typed errors.

For example, in a Result-returning function, a nested validation branch may
abort with `InvalidInput`. An operation sequenced after that branch in the same
function must not run. If its caller matches `Err(InvalidInput)` and chooses a
fallback, the caller may continue. A called helper aborting does not
implicitly abort its caller; the caller must explicitly propagate or recover.

## Graph typing, effects and ordering

- A non-Result function or a mismatched error type rejects before publication.
  Generic error types use the function's existing type parameters; no implicit
  type parameter, conversion, exception type or Result wrapping is invented.
- The abort has no normal value. Initially it is allowed only as the result of a
  ValueGraph (including a nested branch or Bind/match body). It cannot be the
  source of an ordinary value-dependency edge, be shared as a value, or be stored
  in a container. Its payload is an ordinary typed value dependency.
- Analysis must distinguish normal graph completion from abrupt completion.
  A conditional/match may have a value-producing arm and an aborting arm; only
  normally completing arms contribute a normal result type, and those types must
  match. If every arm aborts, the parent also has no normal completion. This does
  not introduce a user-visible `Never` type or a fabricated default value.
- Bind and branch continuations execute only on normal completion. Evaluation
  and lowering must carry the completion distinction through nesting instead of
  hoisting an abort across its branch or scheduling a continuation independently.
- Abort itself has no new nominal EffectId: it is typed, local control flow.
  Payload evaluation and all stored effectful nodes still require their declared
  effects. Validation does not remove effect requirements using reachability or
  prove away an invalid branch because another branch can abort.
- Existing `AmbiguousEffectOrder` rules still reject unsequenced effects.
  Abort must not be used to legitimize backend-dependent sibling ordering.
- No resumable function value, captured abort target, or implicit propagation
  through FunctionInvoke/CapabilityInvoke is added. A pure Result-returning
  function may remain pure if its payload and other computations are pure.

A propagation shorthand that unwraps `Ok` and aborts on `Err` may be considered
later. The first contract requires explicit propagation and does not claim to
eliminate every Result match from a multi-call pipeline.

## Resource lifetime and competing failures

The initial construct composes with the existing
[UseResource contract](expression-authoring.md): its body is a separate function.
An abort in that body becomes a normal typed `Err` outcome at the body-function
boundary. The surrounding UseResource then releases the successfully acquired
resource exactly once before returning that outcome. Code in the aborted body
after the abort does not execute; the enclosing caller may explicitly inspect
the UseResource result and continue.

| Situation | Required outcome |
| --- | --- |
| Acquisition returns Err or aborts its owning invocation before acquisition succeeds | Do not call release for that acquisition. |
| Body aborts, release succeeds | Return the body's typed error. |
| Body aborts, release returns a typed Err | Preserve the body's typed error; do not replace it with the release error. |
| Body succeeds, release returns a typed Err | Return the release error, as today. |
| Body aborts, release throws a host fault | Propagate the release host fault under the current runtime-fault policy. |
| Body throws a host fault, release also fails | Preserve the original body fault, as today. |

Release is mandatory cleanup, not a resumable ordinary continuation after abort.
Nested scopes release in their existing innermost-first order. An abort in a
release helper is local to that helper and produces its declared Result; it does
not bypass other required cleanup. A release operation returning Unit cannot
acquire a typed abort result without an explicit signature change.

## ParallelAll

A worker already returns `Result<U, E>`. Aborting a worker therefore produces
`Err(E)` for that worker, after its own entered resource scopes have cleaned up.
The join keeps the [existing contract](parallel-capability-sharing.md):

- observing a worker Err requests cancellation of other workers;
- cancellation never means those workers have already stopped;
- every started worker is joined before ParallelAll returns or faults;
- among typed failures, the lowest source-index Err wins, independently of
  completion timing; explicit ResultErr and abort-derived Err use the same rule;
- host faults retain their existing precedence over typed failures;
- a resource scope surrounding ParallelAll releases after all workers finish.

An abort is not a new interruption channel for tasks. The first observed Err
means the outcome observed at the worker-function boundary, not the instant an
Abort payload was evaluated inside a resource body. A worker that ignores
cancellation can delay completion indefinitely, just as today. No timeout,
preemption, error aggregation or deterministic ordering among competing host
faults is added.

## Authoring, review and implementation gates

Implementation must retain system-owned node IDs, exact proposal/base binding,
reviewed publication, and existing function/parameter identities. A targeted
replacement can introduce an abort only if the resulting whole function passes
typing, ordering and full snapshot validation. An aborted expression cannot be
replaced with a fake normal value to satisfy an existing use.

Focused reads and review must show the owning FunctionId, abort node, error
payload/type, and affected continuation. These are semantic facts, not an
inferred claim that every runtime path reaches the abort. Diagnostics must
identify a wrong owner result type, payload mismatch, or invalid value use with
semantic IDs; an unsupported backend must report readiness failure instead of
changing the graph.

The maintained implementation is verified against these gates:

1. Define exact recipe and snapshot/Nyason/IR encodings with explicit new schema
   versions. Old readers must reject new constructs. Existing valid graph meaning
   and old canonical encodings must remain intact.
2. Add ordinary exact-ID and name-resolved authoring, recipe discovery, inspect,
   review, targeted replacement, commit and reopen coverage.
3. Prove nested Bind/match abort skips the enclosing function's later effects,
   while ResultErr can be matched in that same function and a caller can recover
   from an aborted callee.
4. Check invalid owner type, error type and value uses, including malformed saved
   graphs that bypass authoring.
5. Execute successful acquisition/body abort/release, acquisition failure,
   nested cleanup, body/release typed errors and host faults with observable
   call sequences. No remaining effect of the aborted body may execute.
6. Execute multiple failing workers in different completion orders, including
   cooperative cancellation and cleanup. Observe all joins and the same
   lowest-index typed error. Verify the surrounding resource releases last.
7. Preserve behavior across save/reopen and deterministic lowering, using the
   existing common runner and failure diagnostics.

A backend may implement local transfer using an explicit IR terminator or a
private runtime mechanism, provided it preserves these observations. Host
exception catches must not accidentally consume an internal transfer, and a
backend's mechanism never defines a new semantic failure category.

## Representation and compatibility

| Contract | Abort representation | Version |
| --- | --- | --- |
| Exact-ID and name-resolved recipe | `{"kind":"abort","error":<recipe>}` | Additive recipe kind in the maintained closed protocol; old servers reject unknown kinds. |
| Snapshot JSON | `{"id":<nodeId>,"kind":"abort","errorNodeId":<nodeId>}` | 11 when an Abort node is present. |
| Nyason | `abort` record with `id` and `error` node identities | `nayamnyam.snapshot.v7` when Abort is present. |
| Typed Semantic IR | `kind: "abort"`, `errorNodeId`, `originNodeId`, `completion: "abort"`; no normal `type` | 8 when Abort is present. |
| Execution IR | Block terminator `kind: "abort"`, `originNodeId`, `errorValueId` | 7 when an abort terminator is present. |

`GraphCompletion.Normal(TypeRef)` and `GraphCompletion.Aborts` are derived facts,
not new language types. Typed structured nodes with no normally completing arm
also carry abrupt completion, and lowering creates no value or merge parameter
for them. `TypedSemanticNode.Completion` replaces its record `Type` field; the
read-only `Type` accessor is only valid for normally completing nodes.

The C# backend emits a function-local `return NyResult<...>.Err(payload)` at the
terminator. No internal exception is thrown or intercepted, so existing resource
and parallel runtime code receives the same typed Result as from an ordinary
callee. Payload instructions run once in their owning block.

Graphs without Abort retain their previous canonical bytes: JSON 9 / Nyason v5,
or JSON 10 / Nyason v6 with CapabilityConstruct; Typed IR 7 and Execution IR 6.
Older snapshot/Nyason versions reject an Abort even if the input has been relabeled
with an older schema. Fingerprint algorithms/domains are unchanged; the existing
canonical serialization includes the new node and version in new graph digests.

Focus, function creation/body replacement review, and targeted review expose
`aborts`: owning function/node IDs, error node/type, enclosing control IDs and
value-dependent continuation IDs. These continuations describe graph dependence,
not a proof of runtime reachability. HTML Focus and Change Review expose the same
boundary facts. Recipe discovery is shared by the MCP and OpenCode tool hosts.

Abort-bearing functions must pass the shared dependency-order rule during
snapshot validation, including deserialize and publication. Legacy graphs with
only ordinary effects retain their existing lowering-time readiness check.
A control that may abort counts as observable for ordering without becoming a
nominal EffectId. Invalid branches and undeclared effects are still validated.
