# Task-local cancellation to Result

## Snapshot migration checkpoint

`fixtures/task-outcomes.nyrec` is the fixed-identity schema-21 snapshot in the
record form: `migrate` first produced the schema-21 JSON copy
(`fixtures/task-outcomes.json.migration.json` records unchanged snapshot/semantic
fingerprints), then `migrate --to` converted it
(`fixtures/task-outcomes.nyrec.migration.json`, same fingerprints; display paths
are repository-relative).
`fixtures/current-proof.json` records fresh replay of the schema-21 JSON copy
before its conversion (its `snapshotSha256` is of those JSON bytes; the record
form keeps the same snapshot and semantic fingerprints):
12 virtual controls and 44 real compiled controls, with unedited emitted C#.
The older proof/identity/verification/runtime records authenticate the original
snapshot bytes and remain at tag `archive/legacy-examples-pre-cutover-20261019`;
they were not relabeled as evidence for the migrated bytes. No fresh moved-assembly
byte-equivalence claim is made by this checkpoint.

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

From the repository root, with the Zig session host built (`npm run build:host`) and locked
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
