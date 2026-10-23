# Lexical cancellation shield

`CancellationShield(body, context)` runs a body with exactly one unbound `Unit`
parameter and result `Result<T, E>`. The context binds its remaining parameters.
Type parameters and body effects follow the existing callback rules. The node
is intrinsically effectful even when it declares no nominal effects, so ordering
requires `Bind` and pure list callbacks cannot hide it.

The context evaluates under the caller's ambient token **before** masking.
The body runs synchronously in the current task with a non-cancelled ambient
token. Its `CancellationRequested` observations, external effects reading
`NyRuntime.Cancellation`, and new nested task scopes therefore do not observe
parent cancellation during that lexical body. Nested shields are allowed.
On `Ok`, `Err`, typed abort or host exception, the original ambient token is
restored. The exact body value or exception is preserved. An already-cancelled
caller can enter a shield; its cancellation remains requested after exit.

Cancellation remains cooperative. This node does not automatically throw,
invent an error value, or capture faults. A caller that needs cancellation to
win after successful cleanup checks `CancellationRequested` after the shield
and returns its own typed error. It can keep an existing cleanup error ahead of
pending cancellation. The [example](../../examples/cleanup-shield/README.md)
uses that rule to model pinned Python `await_cleanup` with normalized data.

A scope created before entry retains its own captured token. Starting work in
that scope inside a shield does **not** detach it from its parent. A scope created
inside the shield links to its masked token; scope exit still cancels and joins
every task before returning. Explicit `TaskCancel` on such a task still works.
The shield changes neither scope ownership nor `UseResource` release/fault
precedence. Resource release is protected only when it executes inside a shield.
An outer `UseResource` release after the shield sees the restored caller token.

The body must terminate or honor its own explicit cancellation mechanism. A
non-cooperative effect can hold the shield forever; there is no forced timeout,
detached-task ownership, transaction lock, persistence or rollback guarantee.
Shield only the bounded cleanup or mutation tail whose completion is required.

Symbolic recipe:

```json
{"kind":"cancellationShield","body":"finish","context":[{"parameter":"host","value":{"kind":"parameter","name":"host"}}]}
```

The ID-based recipe uses `bodyFunctionId` and parameter IDs. Snapshot JSON and
both IRs use `cancellationShield` with `bodyFunctionId` and `context`. Snapshot
schema 15 (`nayamnyam.snapshot.v11`) is required only for graphs
containing this node. Earlier graphs keep their existing versions and identities.
The C# emitter saves `NyRuntime.Cancellation.Value`, assigns `CancellationToken.None`,
calls the body with `NyUnit.Value`, and restores the token in `finally`. Reachability,
effects, parameter migration and viewer inspection include the body callback.
