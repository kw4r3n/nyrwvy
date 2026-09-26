# Parallel capability sharing

**Status:** Current

This contract defines shared capability values in `ParallelAll`. It supplements
[expression authoring](expression-authoring.md); it does not depend on C# task,
lock, or memory-model details.

## Values, ordering, and visibility

- Source and context expressions finish before workers start. A context binding
  shares the evaluated value with every worker. A capability is neither cloned
  nor given a separate provider/state per worker. Aliases, including capabilities
  carried inside items or composite values, refer to the same authority and state.
- Workers may overlap. There is no worker start, operation invocation, or
  completion order based on source index, and no fairness or degree-of-parallelism
  guarantee. Source order governs the result list and failure selection only.
- `Bind` and other sequencing within each worker keep their usual meaning.
  Several operations in a worker do not form a transaction with respect to other
  workers. Cancellation does not undo completed effects.
- A provider used by workers must support concurrent calls across **all** its
  operations and all aliases of an instance, including nested workers. It must
  protect shared mutable state. When one call finishes before another starts,
  its completed updates must be visible to the later call, including calls after
  the join. State shared between distinct provider instances
  must be protected too. Atomicity of a compound operation and the outcome of
  overlapping calls belong to that operation's documented contract; thread safety
  alone does not promise deterministic results or multi-operation transactions.
- `ParallelAll` joins every worker before returning or propagating a host
  exception. A later operation can observe the completed effects. `UseResource`
  surrounding the join releases after all workers finish. A release inside a
  worker can itself overlap other workers and needs a concurrent provider.

The backend does not silently serialize entire capability calls. Such a lock can
deadlock a call awaiting cancellation or another worker. Providers synchronize
their own state and cooperate with cancellation; no scheduler or ownership system
is added here.

## Binding admission and diagnosis

Concurrency support is an explicit **host binding assertion**, not a new property
of a semantic Capability declaration. The same program can use a sequential
provider outside workers or a concurrent provider inside them. Declared effects,
semantic validity, saved graph identities, and fingerprints are unchanged.

The C# profile's `ConcurrentCapabilityIds` asserts the contract above for every
instance supplied under each named capability. It defaults to empty. Bind every
operation as usual, then use `CSharpBackendProfile.withConcurrentCapabilities`, or
the ordinary `emitCSharp` protocol:

```json
{
  "kind": "externalImplementations",
  "operationIds": ["<operation ID>"],
  "concurrentCapabilityIds": ["<capability ID>"]
}
```

An unknown or unbound asserted capability is rejected. Before emission, the
backend follows every `ParallelAll` worker transitively through direct calls,
List callbacks, resource bodies, and nested parallel calls, including cycles.
Every capability invocation and resource release found must have the assertion.
Otherwise `ConcurrentCapabilityBindingRequired` identifies the invoking
FunctionId, node ID, CapabilityId, and CapabilityOperationId. The wire diagnostic
exposes those IDs in `details`. Simulation and publication may succeed: this is a
binding-readiness failure, not a semantic validation error.

This is a conservative capability-type check, not alias or path analysis. It
applies even to a known empty/single-item source, an untaken branch, or an instance
that a host knows is worker-private. Passing an unused capability alone does not
require the assertion. Calls evaluating the outer source/context and release
after the outer join need no assertion unless they are themselves inside an
enclosing worker. `emitCSharp` checks the complete emitted program.

The assertion is consumed by validation; it is not proof of host correctness.
An implementation falsely asserting support violates the binding contract.

## Common runner and a stateful fake

`runPure` checks and emits the selected entry's reachable functions. It preserves
the full type/capability declarations and their existing readiness checks. An
unrelated parallel function does not make a sequential entry require concurrent
host support.

The runner's scripted `fakeCapability` satisfies the shared-state contract. Each
instance owns one response cursor per operation. Recording a call and consuming
its next response happen atomically together; returning, throwing, or waiting for
cancellation happens after that short critical section. `calls` therefore records
response-consumption order, not worker completion or source order.

For example, share `Jobs.run : String -> Result<String, String>` between workers
for `["a", "b"]`, with scripted responses `Ok "ticket-1"`, `Ok "ticket-2"`:

| First call recorded | Result in source order |
| --- | --- |
| `run("a")` | `Ok ["ticket-1", "ticket-2"]` |
| `run("b")` | `Ok ["ticket-2", "ticket-1"]` |

Both results are allowed. Each ticket is consumed exactly once. A subsequent call
on that same fake consumes response 3, rather than starting a new per-worker queue.
The executable example and admission tests are in
[`ParallelAllTests.fs`](../../tests/Nyrwvy.Core.Tests/ParallelAllTests.fs).

A `hostCapability` requires `"supportsConcurrentCalls": true` to assert the same
contract; omitted or `false` means sequential support only. Every supplied host
instance of a capability used in workers must assert support. A concurrent fake
or another host of that type cannot override a sequential instance. Rejection
happens before building or invoking host code. Call logging on a host wrapper
records invocation admission only; the provider controls its own operation order.

## Program-defined implementations

The [program implementation slice](program-capability-implementations.md) admits
only stateless, transitively pure mapped functions. These values can be shared
without adding a host assertion to the semantic graph. The existing backend
profile still checks all worker operations, and the common runner still rejects
an unsafe host instance even when the same Capability type also has a program
implementation. Selecting a pure implementation does not erase the operation's
nominal effect or impose an order between workers.
