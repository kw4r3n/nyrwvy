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
node --experimental-strip-types --test .opencode/tests-extended/nyrwvy-authoring-exception-boundary.test.mjs
```

No real provider, account, filesystem recovery or integration Git effects run.
The mock's five/ten-second waits are failure guards, not timing assertions.

The compiled fixture now has 37 controls. Sixteen classification cases cross
clear/cancelled ambient tokens with ordinary misleading-message faults, OCE
wrappers, OCE with absent/clear/cancelled/other tokens, TaskCanceledException and
an OCE subclass. The selective handler recovers ordinary faults and rethrows
cancellation with original identity and host stack. A pure helper and authored
inverted/constant handlers are compiled in the same unedited source and expose
policy sensitivity. Ok/typed Err/Abort bypass Catch; earlier body/context,
cleanup/drain and release priority controls remain in the fixture. Virtual fake
throws remain ordinary exceptions and do not establish exception class.
