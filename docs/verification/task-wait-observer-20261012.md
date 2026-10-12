# Generic cancellable task observation — 2026-10-12

Observed result: the [reusable graph composition](../../examples/task-wait-observer/README.md)
lets an independently cancelled caller finish while its work tasks and settlement
monitor remain pending. Direct TaskWait semantics, worker tokens and scope
cancel/join drainage are preserved. This is a maintained generic extraction of
the Member follow-up, not a direct TaskWait runtime change or a new Team parity claim.

## Scope decision and identities

The authoritative [TaskWait specification](../specs/expression-authoring.md)
enumerates completion and timeout as its return conditions. The
[accepted concurrency decision](../decisions/concurrency-control.md) keeps
cancellation cooperative and represented by each worker's own Result. In the
current emitter, NyTasks.Wait observes task readiness/time only; NyTaskScope.Close
cancels and unconditionally joins every owned task. The
[Member gap](member-lifecycle-offline-20261012.md#gap-1-caller-cancellation--minimal-reproduction-and-fix-design)
proposes an early partial return, but explicitly identifies its compatibility
risk and is not an accepted replacement for that contract.

Returning partial snapshots when a caller is cancelled would change existing
graphs' wait conditions and require a new completion/fault/cancellation policy.
This task therefore takes the conservative, contract-compatible option: four
ordinary generic U/E graph helpers for Any/All monitoring, cancellable observation
and owner supervision. No `src/` files, schemas, dependencies or fault defaults
changed. The spec now documents the existing direct behavior and links the recipe.

- Repository: `https://github.com/kw4r3n/nyrwvy.git`.
- Fetched and independently checked remote main, including PR381:
  `6215a32d938d76f038fe3cd9201e641b62611556`.
- Main was checked again before publication and retained that identity.
- Publication branch: `codex/taskwait-caller-cancellation`; it did not exist on
  the remote and no open PR duplicated it when checked before publication.
- Authenticated GitHub login verified as `kw4r3n`, without reading credentials.
- Original checkout `/home/kw4/Work/Tools/nyrwvy` was clean at
  `90e495c75dd6915440ea22c96222aefcf04f723e` and was not edited or checked out.
- Isolated worktree based on fetched main; unrelated task-3 artifacts were not used.

## Verification

All execution was local. No saved cloud environment was required. Installed tools:
Nushell `0.116.1`, Node `v26.11.1`, .NET SDK `10.0.112`, runtime/reference pack
`10.0.12`, Roslyn `5.0.0-dev`, 167 reference assemblies. Generated C# was compiled
with nullable checks and warnings as errors, using installed compiler/reference
files without a restore in the runtime harness.

| Check | Observed result |
| --- | --- |
| Release solution build, single MSBuild node | Pass, 0 warnings/errors |
| .NET fast | 854/854 |
| .NET extended aggregate | 123 core execution + 31 extended-project tests passed |
| Reopened virtual observer fixtures | 14/14 |
| Copied snapshot replay | 14/14; same source/symbols and unchanged snapshot bytes |
| Real unedited emitted C# | 14 graph cases + 11 gated/runtime controls passed |
| `npm test` | 8 adapter + 37 Node/script + 12 MCP tests passed |
| `npm run typecheck` | Adapter and Bun declaration checks passed |
| Whitespace check | `git diff --check` passed |

The new regression is in the existing ordinary-authoring Node glob. It allocates
fresh stable IDs, saves/reopens the snapshot, re-emits exact source and symbols,
copies and replays the graph, and checks the source hash after compiling/running
that same source with a separate harness. Temporary snapshots and binaries are
removed after the test; the reproducible recipe/test are committed, not private
runtime artifacts. The graph owns scopes, work handles, monitor, cell and observer.
The real harness adds synthetic effects and gates, plus direct generic-helper
controls using `Result<Int64,Bool>` alongside the graph's String/String instance.

Coverage includes Any/All, finite/zero/negative/unbounded limits, empty and already
settled lists, escaped settled handles, reversed input order, worker Err values,
caller cancellation before publication, publication then cancellation before the
final check, completion returned before later cancellation, a later fault after
caller cancellation, settled faults versus cancellation/zero limits, fault
snapshot order versus scope start order, normal owner exit and external parent
cancellation. Reports assert worker tokens remain uncancelled after caller
cancellation/timeout. Resource release checks all workers have drained. A held
cleanup gate keeps a parent-cancelled worker and its All monitor pending until
cleanup is released; both are complete after scope close.

The direct wait control cancels only its caller, confirms it remains pending with
the worker token unchanged, then releases the worker and checks its completed
snapshot. Raw fault controls verify exceptions survive both supervision and scope
exit. Gate/pre-established states determine race order; no sleep-based race or
wall-time completion ordering is claimed. Timeout cases keep workers gated until
scope cancellation so the expected partial snapshot is independent of thread speed.

VSTest initially hit the sandbox's blocked loopback socket, and synchronous Node
child waits hit `EPERM`; approved local execution passed both. The first Node
aggregate found an MCP client missing from copied old dependencies. A locked
`npm ci --ignore-scripts` restored dependencies in the isolated checkout, and the
full aggregate then passed. No dependency manifests or lockfiles changed.

Reproduction commands (repository root, Nushell-compatible):

```nu
dotnet build Nyrwvy.slnx --configuration Release -m:1
dotnet test tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj --configuration Release --settings tests/fast.runsettings --no-build --no-restore
dotnet test Nyrwvy.slnx --configuration Release --settings tests/extended.runsettings --no-build --no-restore -m:1
npm ci --ignore-scripts --no-audit --no-fund
npm test
npm run typecheck
```

## Explicit limits

Direct TaskWait caller cancellation remains pending; users opt into the observer
composition. An unbounded All monitor retains the completion rule even if another
input faults, so owners must finish/stop remaining work or use a limit. Supervision
observes monitor faults once that monitor can observe its handles; it does not
add early fault detection or capture. Owner closure still waits for cooperative
cleanup and cannot force a worker that ignores cancellation to terminate.

The caller-selected error combines no packet and observed cancellation; it does
not distinguish timeout/closed-owner causes. Cancellation after its final check
cannot retract a returned value. Refused publication after owner closure is not
retried. No general host exception isolation, provider boundary changes, concurrent
controller admission, live-state restart, real Git/provider effects or rrawrlet
oracle rerun are included. The frozen Member fixtures and their original two
direct-runtime divergences remain unchanged. No provider calls, Library uploads,
merges, branch deletion or workflow dispatch were performed.
