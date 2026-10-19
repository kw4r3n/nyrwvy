# Cancellable task observation

Use [author.mjs](author.mjs) to publish four reusable generic graph functions
through ordinary `simulateCreateFunctionByName` and `commit` calls. Its
`defineTaskWaitObserver(create)` callback receives `(name, parameterTypes,
resultType, body)`; publish each in order with `requiredEffects: []`. Normal
authoring allocates fresh U/E type parameter identities for each function.
The [regression test](../../.opencode/tests-extended/nyrwvy-authoring-task-wait-observer.test.mjs)
shows the callback, concrete graph assembly and local execution.

This composes existing TaskWait and OneShot operations. It preserves the current
TaskWait contract and adds no runtime primitive, dependency or fault catcher.
The earlier [Member follow-up](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/member-lifecycle-followup/README.md) established
the pattern; these helpers contain no Member, provider, workspace or Team policy.

## Ownership and usage

For `R = Result<U,E>` and `S = List<Option<R>>`:

1. Keep the work tasks in an owning TaskScope. Create one `OneShot<S>` in that
   owner and start `observeTasks_any` or `observeTasks_all` there. Bind its
   `tasks`, `signal` and `limit: Option<Int64>` context, leaving its Unit item
   unbound. It waits on the actual handles, publishes their ordered snapshots,
   and returns `Result<S,E>.Ok(snapshots)`.
2. Start `awaitTaskObservation` with that signal, an independent wait limit,
   and a caller-selected `noValueError: E`, leaving its Unit item unbound.
   The observer may live in a separate caller scope. Cancelling only its handle
   wakes OneShotWait and returns Err without cancelling the work or monitor.
3. The owner calls `superviseTaskObservation(monitor, caller)`, then joins the
   caller to consume its Result. Supervision watches both handles: a monitor
   exception must reach the owner instead of leaving it waiting only on a cell
   that cannot be published. Normal supervision return means progress, not
   necessarily caller completion. Retain the monitor handle for shutdown and
   fault observation, including after an individually cancelled caller returns.
4. On shutdown, stop or finish the work, then let the owner close and join all
   tasks. An unbounded monitor cannot be abandoned merely by cancelling it:
   direct TaskWait still needs its work handles to settle. UseResource release
   belongs outside that scope so it runs after drainage.

Sequence dependent calls with Bind. Intrinsic task/OneShot observability follows
helper calls even though they do not declare a nominal effect. Each observation
costs one monitor, one cell and the caller task; this example is bounded to one
observation, with no accumulating controller registry.

## Results and precedence

Monitor limits have TaskWait semantics: absent/None is unbounded, negative is
zero, Any/All on an empty list is immediately empty, and every snapshot follows
input handle order. Some carries the worker's actual Result, including Err;
None denotes a still-running task. A worker publishing completion data before
return would not prove task settlement, so it cannot replace the monitor.

An observer limit has OneShotWait semantics. Err with `noValueError` means no
packet observed or cancellation observed at the final check; it does not
distinguish timeout from owner closure. A caller needing that distinction should
check CancellationRequested and choose its own error policy. This helper gives
observed cancellation priority even over Some. Cancellation after the final
check cannot retract an already returned Ok. OneShot itself still gives a
completed value priority at its final observation. Publication refused after
owner closure is not retried; the monitor still returns its actual snapshots.

Raw host faults remain exceptions. Once TaskWait reaches its observation point,
a faulted handle wins over timeout or a cancelled ambient token; fault selection
follows input order. Supervision and scope exit preserve that fault, and scope
exit drains all tasks before rethrowing the earliest-started task's fault. A
cancelled caller may have returned Err before a later work fault; that does not
suppress the owner's eventual exception. No errors are captured as data.

**All limitation:** an unbounded All monitor waits for every input handle,
including after another input faults. Supervision detects a faulted monitor,
not arbitrary faults before that monitor's TaskWait observation point. Owners
that need a bound must supply a monitor limit or stop/finish the remaining work.
Cancelling a caller never supplies that bound. Cooperative workers that ignore
cancellation can still prevent scope drainage; this composition does not impose
a new fail-fast policy.

## Verify locally

From the repository root, with Node 22.6+ and installed .NET 10:

```nu
dotnet build tools/Nyrwvy.AuthoringSession --configuration Release -m:1
node --test .opencode/tests-extended/nyrwvy-authoring-task-wait-observer.test.mjs
```

The test uses the repository's AuthoringSession transport, temporary snapshots,
synthetic capability fakes and installed Roslyn/reference assemblies. It
saves/reopens, copies/replays, compares source and symbols, then compiles the
emitted source unchanged with a separate test harness. Race tests use gates or
pre-established completion/cancellation, not sleeps or random timing. Real
timeout cases keep work gated throughout the wait; they do not assert a
millisecond scheduling edge. The test participates in the existing
ordinary-authoring Node glob; no new workflow or suite partition is added.

See [verification evidence](../../docs/verification/task-wait-observer-20261012.md)
for observed counts, identities and limits.
