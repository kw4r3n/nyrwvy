# Explicit host exception boundary

`ExceptionCatch` runs a callback with one unbound Unit parameter. On a host
`System.Exception`, it invokes a handler with one unbound opaque `Exception`
parameter. Both callbacks return the same `Result<T, E>` after contextual type
substitution; their effects must be declared by the caller. Separate body and
handler contexts evaluate before the protected invocation. Failures in those
contexts bypass this boundary. A handler exception propagates to an enclosing
boundary, rather than invoking the same handler again.

The zero-argument builtin `Exception` holds captured dispatch information.
`ExceptionMessage` reads its message. `ExceptionRethrow(value, resultType)`
rethrows the original exception object and retains its dispatch stack;
`resultType` ascribes its typed expression position, with no runtime return value.
The type is opaque, non-equatable and run-local. It cannot cross capability or
run-value boundaries, including through named types and containers. There is no
constructor, exception class test, automatic translation or exception persistence.

Catch is explicit and includes `OperationCanceledException`. Catching an OCE
does not clear/request cancellation or prescribe an Err. A handler may rethrow
it unchanged or deliberately choose a result. Typed Err and function Abort
are Result control flow, so neither invokes the exception handler. Exceptions
outside a boundary continue to propagate through existing ownership rules.

Catch/rethrow are ordered control effects. Message access is a pure lookup.
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
empty and sorted canonically. Snapshot JSON v16 / Nyason v12 are the minimum
versions for these nodes or the Exception type; older graphs keep their schemas.
The informational typed and execution IR version is 11 for exception-bearing
programs. These IR dumps remain write-only inspection formats.

See [the generic fixture](../../examples/exception-boundary/README.md) and
[controller verification](../verification/member-controller-20261012.md).
