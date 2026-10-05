# Configured request batch

A small program reads JSON configuration, requests three local responses, retries missing responses, uppercases successful values, aggregates successes and expected failures, and saves a report. `UseResource` releases its file handle on success, expected failure, host exception and cancellation.

From the repository root:

```sh
npm ci
dotnet build tools/Nyrwvy.AuthoringSession --configuration Release
node examples/config-request-batch/run.mjs
```

The last line prints its temporary output directory. An optional argument chooses an empty output directory. The script uses the ordinary `nyrwvy_*` tool handlers to author, review and commit graph proposals, then changes one report-label node through `replace` and checks that the function ID stays stable. It never edits a snapshot or generated C# directly. These commits publish graph proposals inside the snapshot; they do not create Git commits.

`original/` and `moved/` each contain the program (`batch.json`), its pinned dependency, `cases.json`, `Host.cs`, an unedited `Generated.cs` projection and local fixtures. The pure uppercase function comes from a separately authored dependency. After copying the bundle, the script renames that dependency's original source and reopens the moved program, so success requires the saved pin. Tool exchanges are recorded in `transcript.jsonl`.

Five saved cases run after reopening, and again after moving:

| Case | Checked outcome |
| --- | --- |
| Success | Three uppercase responses and exact saved report |
| Retry and missing response | Two successes, one expected failure; exact attempts and report |
| Invalid configuration | `Err("maxAttempts must be positive")`; no report |
| Host exception | Reading a directory as a response raises `RunRuntimeFailure`; no report |
| Cancellation | Cancel after a request enters its host boundary; `Err("cancelled")`; no report |

Every case checks exactly one `close disposed` trace entry and a disposed stream. The saved cases are ordinary `runPureByName` requests with relative fixture roots. To rerun them later, including from another copied bundle:

```sh
node examples/config-request-batch/run.mjs --replay /path/to/output/moved
```

Replay rewrites only the fixture reports and traces and appends the tool transcript. It does not author the program again.

The host also owns cleanup when acquisition itself throws, before `UseResource` has received a handle. A focused regression forces `trace.txt` to be a directory, checks that the original trace I/O exception retains its path, directly checks the private handle collection is empty, opens the exclusive lease file again, and successfully reacquires and releases through the same host instance. It compiles the bundle's unedited projection and host in a temporary directory with the local .NET 10 SDK, without fetching packages:

```sh
node examples/config-request-batch/test-acquisition.mjs /path/to/output/moved
```

The Nyrwvy graph owns configuration validation, branching, retry limits, transformation, aggregation and cancellation policy. The handwritten C# template implements actual file operations and JSON decoding into typed data; it does not implement these policies. Its concrete generated interfaces and constructor parameter names come from the emitter's symbol manifest, so declaration ordering is immaterial.

The cancellation fixture additionally provides an entry barrier and a request waiting for cancellation. `runPure` schedules concurrency with its deterministic virtual clock; the barrier cooperates through the existing `NyVirtualClock.Block`, and the request uses `NyRuntime.WaitForCancellation`. Without a runner clock the barrier uses a bounded real wait. This case demonstrates cooperative cancellation, not interruption of arbitrary blocking file I/O. The worker is consumed inside its `TaskScope`; no scope handoff or new core abstraction is needed.

All responses are local fixtures. No network, models, provider credentials or paid calls are used. `runPure` host-source execution uses the existing explicitly enabled trusted-host boundary, rather than an OS sandbox. This example has no transaction or crash-recovery guarantee for report writes.
