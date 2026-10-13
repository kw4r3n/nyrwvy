# Task-local cancellation-to-Result composition

The bounded composition is expressible with existing recipes. The
[fixture](../../examples/task-cancellation-outcome/README.md) catches host
exceptions inside the callback passed to `TaskStart`. `ExceptionIsCancellation`
selects a program-chosen `Err("stopped-by-program")` of
`Result<String,String>`; the other branch uses `ExceptionRethrow`. This fixture
adds no primitive, runtime state, schema or IR change. Existing Member
Final/default behavior is unchanged.

## Boundary placement and ownership

The real C# host throws an OCE with a clear token, or throws from the captured
worker token after a gated `TaskCancel` or root cancellation. For each case:

- Baseline: the explicitly joined child is faulted, `IsCanceled` is false,
  Snapshot/TaskWait rethrow the original object with its host stack, and
  `Close(false)` rethrows that original object.
- Catch around TaskWait only: the local wait invocation returns the chosen Err,
  but the joined child remains faulted; Snapshot and scope Close still rethrow
  the original OCE. The full UseResource entry also propagates it.
- Worker-local adapter: the explicitly joined child completes successfully with
  the chosen typed Err; Snapshot and TaskWait report it, and scope Close returns
  normally. The captured worker token keeps its request state before Close.

The last token observation deliberately precedes scope Close, which requests
cancellation under the existing contract. The join helper shields only waiting,
so an already-cancelled owner can finish joining; it does not replace the
already-captured child scope token.

An ordinary exception containing a cancel message and an ordinary wrapper with
an inner OCE both rethrow unchanged, with identity and host stack checks, under
clear and cancelled parents. OCE, TaskCanceledException and an OCE subclass
adapt. Existing Ok and typed Err pass through. A held sibling remains running
with its token clear after the individually cancelled worker settles Err; it
finishes only after explicit host release and join.

Cleanup is after the worker catch. A held cleanup prevents owner completion and
resource release; its ordinary fault or OCE escapes unchanged. UseResource
release runs once after drainage and its fault escapes after a normal typed
result. A cleanup/body fault retains precedence over a competing release fault.
An unrelated ordinary child fault still escapes with an adapted sibling, and
the earliest-started ordinary fault wins when two faulted children finish in
reverse order. All terminal task observations follow explicit joins. Gate waits
are bounded failure detection, with no sleeps, polling or retry-until-pass.

## Sensitivity and persistence

The 44 real C# controls compile the unedited emitted projection together with a
separate mock host/harness. Wrong-boundary and inverted-predicate variants are
authored as ordinary functions and compiled in the same projection. They fail
the identical cancellation acceptance assertions that the worker adapter
passes. Inversion also fails the identical ordinary-fault assertions. The
retained real evidence includes the three assertion-failure messages.

The 12 virtual cases preserve Ok/Err and ordinary synthetic throw behavior.
Save/close/reopen re-emits identical source and symbols. A copied bundle reopens
at a moved path without changing its snapshot, emitted source or symbols, and
repeats the virtual and real observations. The maintained Node regression also
compares deterministic compiled assembly bytes. The saved snapshot and hashed
proof are retained in [the fixture evidence](../../examples/task-cancellation-outcome/fixtures/proof.json).

Virtual and real execution share the C# emitter/runtime. Fake throws are
ordinary synthetic exceptions and do not establish cancellation-class proof.
There is no Python flag parity claim, new cancelled Task state, broad shutdown
claim, live-provider execution, arbitrary-interleaving coverage or restart
persistence claim. String error payloads are this program's explicit policy;
they do not introduce a nominal stopped-state type or automatic host policy.

## Verification record

Final serial aggregate results and file/log hashes are retained in
[verification.json](../../examples/task-cancellation-outcome/fixtures/verification.json).

| Final check | Observed result |
| --- | --- |
| Release solution build, no restore | Zero warnings/errors |
| .NET fast | 864 passed |
| .NET Extended, separate serial projects | 123 Core + 31 Extended passed |
| Adapter / Node / MCP / Bun | 8 / 47 / 12 / 23 passed |
| Adapter and Bun typechecks | Both passed |
| Suite partition / fish contract and syntax / whitespace | Passed |
| Fixture / moved replay | 44 real + 12 virtual; identical source, symbols, snapshot and assembly bytes |

Full local bundles, JSON-lines transcripts, emitted C#, host source, compiled
assemblies and logs remain under `artifacts/task-cancellation-outcome/` in the
returned checkout. The fresh branch starts from main
`c124e84585652559fc9c88a56cb61cbec0d56876`.

Initial fixture diagnostics found an invalid Unit Result wire encoding and
incorrect harness namespace/string-construction assumptions, which were
corrected. The first substantive runtime attempt passed all nine placement
cases, then rejected an assertion reading token state after scope Close. Since
Close requests cancellation, that observation was moved before Close; the
runtime implementation was unchanged. The failed log remains retained and
hashed. The initial sandbox build failed without diagnostics, and SDK/compiler
execution required expanded local execution; approval review allowed it.
No automatic approval review rejected an action. Final checks use .NET 10.0.112,
Node 26.11.1 and Bun 1.4.3; Node dependencies were copied from the preserved
source checkout rather than freshly installed. These are environment facts,
not a claim that the exact CI Node/Bun pins were used.

The initial typecheck omitted the source checkout's nested MCP workspace
dependencies; copying that existing dependency directory corrected the setup.
The initial solution-level Extended invocation passed but ran both test projects
concurrently despite the per-host concurrency settings. Final Extended checks
invoke the two projects separately and serially. Both initial logs are retained
and hashed; this was an orchestration correction, with no runtime change.

A terminal exec-server disconnect interrupted the initial serial Node aggregate
before a saved result. Recovery confirmed the session and runner/aggregate
processes were gone. Its buffered test output was lost, so no outcome is
counted for that interrupted invocation. Only that aggregate and the remaining
checks resumed, with direct streaming logs. Completed checks were preserved;
no assertion was changed or schedule retried to select a passing result.
After the reported desktop app restart, direct executor reads and process/session
checks succeeded and the resumed verifier was still running; no duplicate was
started at that checkpoint.

The local commit/tree is returned for independent parent review. Review has not
been claimed, and feature push remains pending that review. Parent handles PR.
No credential/access changes, branch deletion, force push, merge or live
provider calls occurred.
