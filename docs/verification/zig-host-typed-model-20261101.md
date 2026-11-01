# Zig host typed-model migration

Working-tree verification against `2be64ee`. The F# host remains the default.

## Implementation

- Session state, pending proposals and dependency selections retain typed generations and interners. `Request.model` and `Dependency.root` have been removed.
- One payload decoder and builder serve graph traversal, immutable edits, inference, validation, lowering and remapping. Live-set rebuilding copies native records and preserves shared bodies.
- JSON document decoding uses a temporary arena. Native reader results own their records, strings, diagnostics and fingerprints after that arena is released. The record reader and dependency-manifest reader use temporary parsing storage as well.
- Startup canonical ordering and semantic insertion order are separate. Response encoders sort ID-keyed collections while preserving parameter and type-argument order.
- Dependency combination compares canonical declaration bytes; upgrade previews compare declaration content in source order. Already decoded dependencies are combined without serialization and reparsing.
- Current-schema writing, minimum-schema writing and normalized fingerprint input are produced from typed records. JSON remains a boundary format for protocol data, immutable reviews, viewer data and Execution IR.

## Verification status

The initial extended run exposed reference-query dispatch and output-order regressions. It was interrupted after the workstation ran out of memory; it is not a passing gate. The final rerun used `DOTNET_PROCESSOR_COUNT=1`. After swap was increased, a second sequence ran the remaining native, fast, adapter and Rhifill checks while the extended suite continued. The benchmark ran alone after both sequences completed.

Completed checks on the final implementation:

- Release solution build: no warnings or errors.
- Fast managed suite: 1,269 passed.
- Extended managed suite: 293 passed (287 Core tests in 32 min 35 s, then 6 end-to-end tests in 15 s).
- Native Node/MCP contracts: 63 passed; native extended Node pipelines: 4 passed.
- Validator differential corpus: 14,847 byte-identical cases. Managed validator and lowering comparisons: 3 passed.
- Direct typed writer comparison covers every node kind, examples, shuffled input, Unicode type parameters, resolved snapshots and both fingerprints.
- Native payload, builder, dependency and semantic-context checks: 17 passed. The resolved native-reader check also exercises nested dependencies and rejected markers after decoder scratch is freed.
- Live-set check: 200 commit, reject, encoder-rollback and cancelled-preparation iterations while retaining a stale proposal. Retained arena/interner bytes were 23,740 at baseline and at most 23,808. This small fixture is not a real-workspace memory benchmark.
- Rhifill copy: 77 saved run fixtures passed; C# source and facade emission with 17 external operations and the Ghostty mapping table succeeded. All 92 original file hashes remained unchanged after these checks.
- Independent defect review completed. Its confirmed finding, temporary interner entries retained during dependency commit revalidation, was fixed with scoped checkpoint rollback; the operation lifecycle and dependency simulation regression group passed all 9 tests afterward.

All 3 `ZigSpeed` tests passed. Results are recorded in [the benchmark log](../../evaluation/zig-speed/README.md#typed-model-host-migration-benchmark-2026-11-01). The live-set fixture above is deliberately small; real-workspace peak memory and other operating systems were not measured here.

## Reproduction

```sh
DOTNET_PROCESSOR_COUNT=1 dotnet build Nyrwvy.slnx -c Release --no-restore -m:1
DOTNET_PROCESSOR_COUNT=1 dotnet test tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj -c Release --no-build --no-restore -m:1 --settings tests/fast.runsettings
DOTNET_PROCESSOR_COUNT=1 dotnet test Nyrwvy.slnx -c Release --no-build --no-restore -m:1 --settings tests/extended.runsettings
NYRWVY_AUTHORING_EXECUTABLE=/absolute/path/to/current/host npm run test:node
NYRWVY_AUTHORING_EXECUTABLE=/absolute/path/to/current/host npm run test:node:extended
```

Run these sequentially. VSTest needs its local communication socket; the process tests also need functioning child-process pipes.
