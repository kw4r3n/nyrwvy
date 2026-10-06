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

## Evolve the program to v2

```sh
node examples/config-request-batch/run.mjs --v2
```

An optional second argument selects an empty output directory. This mode first runs the v1 exercise, then uses existing APIs to make two separately published changes:

1. Revise the original formatting-library path so `normalizeValue` returns `v2:` followed by the uppercase response, preserving its signature and IDs. Reopen the consumer and prove its retained v1 pin still produces the original output. Commit a local `missingLine` label change to `skipped `, preview the dependency upgrade, and apply only a fresh compatible fingerprint through `open(mode: "upgrade")`. The local edit and existing declaration/parameter IDs survive. The upgraded program runs with its original summary type as a valid intermediate state.
2. Use `simulateProductFieldTypeMigration` to change `BatchSummary.failed` from `Int64` to `List<String>`. Exactly `collectRequest` and `processConfig` require complete replacement bodies; their function and parameter IDs and all Product field IDs stay stable. An empty failure list replaces zero, and a missing response appends `beta: missing` after its retry limit. The saved report and all exception/cancellation/resource checks remain explicit assertions.

The script rejects an incompatible upstream result type, a source modified after preview, an incomplete rewrite set, an old inspected snapshot and a mistyped rewrite. Each refusal checks both the snapshot and dependency sidecar are byte-identical. It also closes a valid but uncommitted migration proposal, reopens the last published state, refuses reuse of the old proposal handle and reruns that state before resimulating and committing.

`cases.v1.json` and `cases.v1-local.json` retain historical expectations; `cases.json` holds the explicit final v2 expectations. Historical cases are not silently adapted to the new field type. `evolution.json` records the intermediate states, refusals and preserved identities. The final bundle is moved and run after every upstream source is unavailable. The normal `--replay` and focused acquisition-test commands above work with that bundle too.

The dependency upgrade and field migration are independent publications, not one atomic combined update. A refusal retains the immediately preceding committed state, which may already include an earlier successful step. This exercise adds no rollback manager and makes no machine-crash or cross-file crash-atomicity guarantee. Existing upgrade/migration primitives provide the workflow; no language-core change or agent command wrapper is added.

The host also owns cleanup when acquisition itself throws, before `UseResource` has received a handle. A focused regression forces `trace.txt` to be a directory, checks that the original trace I/O exception retains its path, directly checks the private handle collection is empty, opens the exclusive lease file again, and successfully reacquires and releases through the same host instance. It compiles the bundle's unedited projection and host in a temporary directory with the local .NET 10 SDK, without fetching packages:

```sh
node examples/config-request-batch/test-acquisition.mjs /path/to/output/moved
```

The Nyrwvy graph owns configuration validation, branching, retry limits, transformation, aggregation and cancellation policy. The handwritten C# template implements actual file operations and JSON decoding into typed data; it does not implement these policies. Its concrete generated interfaces and constructor parameter names come from the emitter's symbol manifest, so declaration ordering is immaterial.

The cancellation fixture additionally provides an entry barrier and a request waiting for cancellation. `runPure` schedules concurrency with its deterministic virtual clock; the barrier cooperates through the existing `NyVirtualClock.Block`, and the request uses `NyRuntime.WaitForCancellation`. Without a runner clock the barrier uses a bounded real wait. This case demonstrates cooperative cancellation, not interruption of arbitrary blocking file I/O. The worker is consumed inside its `TaskScope`; no scope handoff or new core abstraction is needed.

All responses are local fixtures. No network, models, provider credentials or paid calls are used. `runPure` host-source execution uses the existing explicitly enabled trusted-host boundary, rather than an OS sandbox. This example has no transaction or crash-recovery guarantee for report writes.

## Evolve the fetch operation signature

```sh
node examples/config-request-batch/run.mjs --signature
```

This mode authors v1, then declares `FetchFailure = Retryable(message: String) | Permanent(message: String)` and reviews one `simulateCapabilityOperationSignatureMigrationByName` proposal. It changes `BatchFiles.fetch` to `Result<String,FetchFailure>` and replaces `fetchWithRetry` together. The caller returns its existing `Result<String,String>` by explicitly extracting the error message. Retryable errors retry to the configured limit, even for messages other than `missing`; permanent errors stop immediately, even when their message is `missing`.

The operation parameter type map and each repaired function's parameter type map cover every retained parameter exactly once. IDs, ordering and names remain stable. This API also supports argument type changes and affected implementation/function signature repairs, demonstrated by maintained regressions. It does not add/remove parameters or alter effects/FailureType.

`signature-evolution.json` records stable identities, rejected incomplete/stale/mistyped requests and explicit old/new fake queues. No equivalence is asserted across changed response types. `cases.before-signature.json` retains the old host cases; final `cases.json` adds a permanent failure with exactly one attempt. Generated host signatures and error constructors come from the symbol manifest. The graph proposal publishes atomically; updating/deploying the external host is an explicit separate boundary action. Reopen, moved pins, exception/cancellation and one release per acquired handle are executed against the final bundle.
