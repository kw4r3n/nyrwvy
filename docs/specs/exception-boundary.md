# Explicit host exception boundary

A **host failure** is a per-task abort that carries a kind, `Cancelled` or
`Fault`, and a message ([decision](../decisions/host-failure-kinds.md)). It
propagates through calls, running `UseResource` releases and restoring
`CancellationShield` tokens, until an `ExceptionCatch` handles it. Allocation
failure and stack overflow are **fatal** instead: they end the run, no
`ExceptionCatch` handles them, and releases are not guaranteed to run. Each
backend realizes host failures its own way; the C# backend uses .NET exceptions,
with `Cancelled` as `System.OperationCanceledException` (subclasses included).

`ExceptionCatch` runs a callback with one unbound Unit parameter. On a host
failure, it invokes a handler with one unbound opaque `Exception`
parameter. Both callbacks return the same `Result<T, E>` after contextual type
substitution; their effects must be declared by the caller. Separate body and
handler contexts evaluate before the protected invocation. Failures in those
contexts bypass this boundary. A handler exception propagates to an enclosing
boundary, rather than invoking the same handler again.

The zero-argument builtin `Exception` holds the captured failure.
`ExceptionMessage` reads its message. `ExceptionIsCancellation(value)` is a pure
`Exception -> Bool` classifier: true exactly when the kind is `Cancelled`. In C#
it tests the captured dispatch information's
`SourceException is System.OperationCanceledException`, including
`TaskCanceledException` and other subclasses. Messages, ambient/exception token
state or identity, and nested `InnerException` do not participate. An ordinary
wrapper around an OCE remains ordinary. The predicate does not request, clear or
mask cancellation. `ExceptionRethrow(value, resultType)`
rethrows the original failure with the same kind and message; a backend keeps
its diagnostic stack where it can (C# rethrows the original exception object
with its dispatch stack);
`resultType` ascribes its typed expression position, with no runtime return value.
The type is opaque, non-equatable and run-local. It cannot cross capability or
run-value boundaries, including through named types and containers. There is no
constructor, general exception type-name/reflection API, automatic translation or exception persistence.

Catch is explicit and includes `Cancelled` failures. Catching one
does not clear/request cancellation or prescribe an Err. A handler may rethrow
it unchanged or deliberately choose a result. To recover ordinary faults while
propagating cancellation, use a Conditional on `ExceptionIsCancellation` whose
true branch is `ExceptionRethrow` and false branch returns the recovery Result. Typed Err and function Abort
are Result control flow, so neither invokes the exception handler. Exceptions
outside a boundary continue to propagate through existing ownership rules.

Catch/rethrow are ordered control effects. Message access and cancellation classification are pure lookups.
This boundary does not mask cancellation, start/cancel tasks, drain a scope,
or release resources. Compose CancellationShield, TaskScope and UseResource
for those policies. A shield restores its caller token before an outer catch
handler runs. Captured scope tokens retain their existing ownership.

```json
{"kind":"exceptionCatch","body":"commitEffect","bodyContext":[{"parameter":"host","value":{"kind":"parameter","name":"host"}}],"handler":"rollbackThenRethrow","handlerContext":[{"parameter":"host","value":{"kind":"parameter","name":"host"}}]}
```

Ordinary recipes use `body`/`handler` function selectors and
`bodyContext`/`handlerContext` parameter bindings. Exact recipes and saved JSON
use `bodyFunctionId`/`handlerFunctionId`. Saved context arrays are omitted when
empty and sorted canonically. Snapshot schema 16 (`nayamnyam.snapshot.v12`) is the minimum
version for these nodes or the Exception type; older graphs keep their schemas.
The informational typed and execution IR version is 11 for exception-bearing
programs. The classifier requires snapshot schema 17 (`nayamnyam.snapshot.v13`) and informational
IR v12; programs without it retain their existing minimum versions. These IR
dumps remain write-only inspection formats.

See [the generic fixture](../../examples/exception-boundary/README.md) and
[controller verification](../verification/member-controller-20261012.md).

A selective ordinary recipe handler returning `Result<String,String>` is:

```json
{"kind":"conditional","condition":{"kind":"exceptionIsCancellation","value":{"kind":"parameter","name":"exception"}},"whenTrue":{"kind":"exceptionRethrow","value":{"kind":"parameter","name":"exception"},"resultType":{"kind":"builtinGeneric","generic":"result","arguments":[{"kind":"primitive","primitive":"string"},{"kind":"primitive","primitive":"string"}]}},"whenFalse":{"kind":"resultErr","error":{"kind":"exceptionMessage","value":{"kind":"parameter","name":"exception"}},"okType":{"kind":"primitive","primitive":"string"}}}
```

The exact recipe uses the same unary `value` field; saved snapshots and both IR
projections use `valueNodeId` / `valueId` according to their existing unary-node
convention. This generic primitive does not prescribe controller policy or alter
task fault/cancellation, cleanup, release priority or retained outcome rules.
See [the classification proof](../verification/exception-is-cancellation-20261014.md).
