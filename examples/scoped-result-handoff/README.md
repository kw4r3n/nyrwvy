# Scoped result handoff

Two request tasks wait independently while a third task receives responses and routes them by request ID. The graph owns dispatch, first completion, timeout handling and shutdown. Transport fixtures supply I/O only. The program uses three [scoped one-shot operations](../../docs/specs/scoped-one-shot.md) and the existing TaskScope; it adds no channel or scheduler.

Run from the repository root after `npm ci` and a Release build of `tools/Nyrwvy.AuthoringSession` with .NET 10:

```sh
node examples/scoped-result-handoff/run.mjs /path/to/empty-output
```

The ordinary authoring tools create and commit the graph locally, emit C#, close and reopen the snapshot, then run six saved cases: reversed responses, duplicate completion, one timeout, receiver expected error, receiver host exception and parent cancellation. Each case checks resource close exactly once; cancellation and host failure do not save a success report. Generic `newCell<T>` and `publishValue<T>` helpers compose through ordinary function calls.

Move the entire output directory and replay without authoring it again:

```sh
mv /path/to/empty-output /path/to/moved-output
node examples/scoped-result-handoff/run.mjs --replay /path/to/moved-output
node examples/scoped-result-handoff/test-runtime.mjs /path/to/moved-output
```

The bundle contains `broker.nyrec`, `declarations.json`, `cases.json`, `evidence.json`, `Generated.cs`, `symbols.json` and the tool transcript. `Generated.cs` is an unedited projection. The real-runtime test binds transport implementations through its symbol manifest, compiles in a temporary directory using the local NuGet cache, and deletes only that temporary directory. It runs the same six cases, plus completed-value cancellation, eight observers, timeouts that preserve cell ownership, escaped terminal handles, foreign-scope waiter cancellation and 200 barrier-controlled completion/completion and completion/close races. It also completes 1,000 payload cells while the owner remains open and verifies that the private pending registry retains only its one unresolved cell. A bounded watchdog applies only to this newly started harness.

`OneShotWait` returns None for timeout, unresolved owner close or individual waiter cancellation. Some has priority when a value is available at final observation. Workers therefore check CancellationRequested after waiting, even after Some; the coordinator checks again before saving. These are cooperative checks, without atomic rollback for cancellation during an external save. A helper with no declared effects retains the existing pure-call ordering rule, so this example explicitly sequences dependent helper calls with Bind.

The predecessor `probe-existing.mjs` records the existing event-loop behavior and why finishing an independent receiver alone does not supply another task's result. `red-create.mjs` is now a positive ordinary-tool regression for authoring, save/reopen and projection of the new cell type:

```sh
node examples/scoped-result-handoff/red-create.mjs /path/to/new-snapshot.json
```

Focused semantic coverage is in `tests/Nyrwvy.Core.Tests/OneShotTests.fs`; the Core suite also checks exhaustive node authority, effect classification and feature-selected schema versions.
