# Explicit host exception boundary

Ordinary graph proposals author an explicit catch callback, captured exception
message access and rethrow. The fixture composes the boundary with existing
CancellationShield, TaskScope, TaskCancel and UseResource operations.

Four virtual cases test explicit recovery, rethrow, typed abort and context
failure. Thirteen controls compile unedited emitted C# and check original
exception identity and throwing stack (including OCE), restored cancellation,
typed abort bypass, outer resource release, held nested drainage, rollback fault
precedence and repeated direct TaskCancel without cancelling the scope parent.
The runtime harness reuses the cleanup-shield fixture's mock host and held gates.
Virtual and real execution share the C# emitter; these are runtime contract checks,
not an independent oracle. The controller example supplies independent Python evidence.

```nu
node examples/exception-boundary/run.mjs /tmp/exceptions-fresh
node examples/exception-boundary/run.mjs --replay /tmp/exceptions-fresh
node examples/exception-boundary/test-runtime.mjs /tmp/exceptions-fresh
node --experimental-strip-types --test .opencode/tests/nyrwvy-authoring-exception-boundary.test.mjs
```

No real provider, account, filesystem recovery or integration Git effects run.
The mock's five/ten-second waits are failure guards, not timing assertions.
