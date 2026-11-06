# Program-defined capability implementations

**Status:** Current

`CapabilityConstruct(capabilityId, implementations)` creates a capability value
whose operations are implemented by existing Nyrwvy functions. It uses the
existing capability type and ordinary function/recipe authoring; a factory is an
ordinary function returning that capability. No separate implementation identity,
handler scope, capture environment, mutable state, or continuation is introduced.

## Meaning and validation

- The mapping contains exactly one `(CapabilityOperationId, FunctionId)` for each
  operation of the capability. Missing, foreign and duplicate operations reject.
  An empty capability accepts an empty mapping.
- Each target is a monomorphic, transitively pure function, under the same purity
  rule as [function values](pure-function-values.md). Its parameters match the
  operation's parameter types **in declaration order**, and its result type
  matches exactly. Parameter names and identities need not match. Use a concrete
  wrapper for a generic function.
- Construction does not execute an implementation. Invocation passes the
  operation's arguments to the mapped function and returns its result. Mapping
  order is not semantic; canonical encodings order mappings by operation ID.
- Construction is pure. Invocation retains the operation's declared EffectId,
  even when the selected implementation is pure. No automatic effect elimination
  or widening occurs. Existing Bind/order checks still apply.
- Expected failures use an explicit `Result` result type. `FailureType` is
  [retired](../decisions/capability-failure-type-retired.md): authoring rejects
  it, and the C# backend rejects an older snapshot that still carries one as
  `UnsupportedCapabilityFailure`.
- A value can be passed, returned, selected in a branch, or stored in existing
  immutable containers like other capability values. It carries no observable
  allocation identity. No equality operation is added.

Full snapshot validation repeats these checks, including on later edits to the
capability or implementation functions. Removing a referenced operation/function,
changing its signature, or making a reachable implementation impure cannot be
published until all affected constructions are repaired in the candidate.

## Authoring and review

Ordinary name-resolved recipes use:

```json
{
  "kind": "capabilityConstruct",
  "capability": "Clock",
  "implementations": [{ "operation": "now", "function": "fixedTime" }]
}
```

Exact-ID recipes use `capabilityId`, `operationId` and `functionId` instead.
Unsupported fields, including capture/state fields, reject. Names resolve to
unique stable IDs at simulation. The recipe is discoverable through
`nyrwvy_recipe_reference` and usable through `nyrwvy_create_function` and
`nyrwvy_replace`; publication uses the existing reviewed proposal.

Focused reads expose the complete operation/function mapping. Caller/callee
queries include every mapped function as a reference, rather than implying that
construction calls it. HTML focus/review labels show the mapping. New expression
IDs are system-owned; targeted replacement preserves the selected node's ID.

## Persistence and execution

Snapshots containing the node use snapshot schema **10**
(**`nayamnyam.snapshot.v6`**). Readers also accept the previous schema **9**
(**`nayamnyam.snapshot.v5`**), but reject the new construct under those old versions. Writers continue
to emit the old versions for graphs without this construct, preserving their
canonical bytes and fingerprints. No migration invents implementation mappings.
Snapshot and semantic fingerprint domains remain v6: identity/name normalization
is unchanged, and the explicitly versioned canonical snapshot includes the new
node and its mapping. Old readers reject the new schema explicitly.

Typed Semantic IR schema **7** and Execution IR schema **6** carry the same typed
mapping. Entry reachability retains all implementations and their transitive
references, including implementations of operations not invoked by that entry.
C# emits a stateless implementation of the existing capability interface per
construction node. It invokes generated Nyrwvy methods using the semantic
parameter correspondence; generated parameter-name ordering cannot reorder the
arguments. Zig emits one capability value type with a selector per construction
node, dispatching beside a runner's scripted fakes.

The backend profile still declares the realization of callable capability
interfaces. `ExternalImplementation` permits values implementing that interface;
a program construction provides such a value without host code. The common
runner already supplies this profile, so internally constructed values require
no `hostSources`, fake responses, or new wire value kind. Raw capability values
remain unsupported as runner results. Runtime function frames identify faults
inside mapped functions; program-defined calls are not host/fake call-log events.

Pure stateless implementations can be shared by `ParallelAll` workers. Backend
profiles still enforce the existing capability-wide concurrent-binding contract;
a program construction never grants an unsafe host instance of the same type a
concurrency assertion. The common runner retains its explicit host-instance
check. State/capture, implementations introducing effects, effect elimination,
and resumable handlers are outside this slice.
