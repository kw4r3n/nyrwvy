# Task-local cancellation to Result

This bounded fixture uses existing `ExceptionCatch` and `ExceptionIsCancellation`
inside a `TaskStart` worker. The program chooses `Err("stopped-by-program")` of
`Result<String,String>` for an OCE (including subclasses); ordinary exceptions
use `ExceptionRethrow`. Existing Ok and Err values pass through. This is an
explicit program policy, with no added runtime task state or Member policy.

| Placement | Joined child | TaskWait | Scope Close |
| --- | --- | --- | --- |
| Baseline | Faulted, original OCE | Original OCE | Original OCE |
| Catch around TaskWait only | Faulted, original OCE | Local chosen Err | Original OCE |
| Worker-local catch | Successfully completed with chosen Err | Some(Err) | Normal |

`cleanWorker` invokes cleanup after the adapter. `UseResource` encloses the owned
scope and releases after drainage. Neither cleanup nor release is covered by the
worker catch. Ordinary body faults retain precedence over competing release
faults; the earliest-started faulted child wins even under reverse completion.
The join helper masks its caller cancellation only while waiting; child scope
tokens are already captured and stay linked to their original owner.

`fixtures/task-outcomes.json` is the snapshot the probe run recorded (at
schema 17; `fixtures/proof.json` names its original sha256). On 2026-10-19 it
was rewritten in place to the current schema 21 by
`Nyrwvy.AuthoringSession migrate --snapshot examples/task-cancellation-outcome/fixtures/task-outcomes.json`
([snapshot migration](../../docs/specs/agent-authoring-session.md#snapshot-migration));
`fixtures/task-outcomes.migration.json` is the report, which shows a
representation-only migration with both fingerprints unchanged. The file's
sha256 therefore differs from the one in `proof.json`, which refers to the
bytes of the original run; the commands below author the program again and do
not read the fixture.

From the repository root, with the Release AuthoringSession built and locked
Node dependencies installed, run each command serially in Nushell:

```nu
node examples/task-cancellation-outcome/run.mjs artifacts/task-outcome/bundle
node examples/task-cancellation-outcome/test-runtime.mjs artifacts/task-outcome/bundle
cp -r artifacts/task-outcome/bundle artifacts/task-outcome/moved
node examples/task-cancellation-outcome/run.mjs --replay artifacts/task-outcome/moved
node examples/task-cancellation-outcome/test-runtime.mjs artifacts/task-outcome/moved
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests-extended/nyrwvy-authoring-task-cancellation-outcome.test.mjs
```

The maintained Node test compares snapshot, emitted source, symbols, observations
and deterministic compiled assembly bytes across the move. Emitted C# is never
edited; the host/test program is generated separately from published symbols.
Real gates control cancellation, sibling release and cleanup. Explicit joins
precede terminal observations; ten-second gate waits are failure bounds, without
sleeps or polling. Compiled wait-only and inverted-predicate controls fail the
same acceptance assertions used by the production composition.

The 12 virtual controls test existing Results and ordinary synthetic faults.
The 44 real C# controls additionally prove OCE classification and object/stack
preservation. Virtual and real execution share the C# emitter; a fake throw is
not cancellation-class evidence. No Python flag parity, live provider, general
shutdown, restart persistence or arbitrary-interleaving claim is made.

See the [verification record](../../docs/verification/task-cancellation-outcome-20261014.md).
