# Removal of the F# session host

Step 5 of the switch in [zig-session-host.md](../decisions/zig-session-host.md#the-switch), second part: `tools/Nyrwvy.AuthoringSession` is removed by the owner's decision. Tag `fsharp-host-final` (fcf787d, on origin) keeps it. The first part removed the check modes and the F# emitter ([record](zig-emitter-check-removal-20261102.md)). Branch `feat/fsharp-host-removal` from `bf60381a` (PR597's head), Linux.

## Change

- **Deleted.** The host project (30 files). 97 test files that tested the host in process: 95 host test files, `ViewerRendering.fs` (it rendered pages through the host's `ViewerData`) and `ZigSpeedTests.fs` (it compared the Zig path with the F# host), with `tests/benchmark.runsettings`. Also deleted are the scripts that loaded the host's DLL: `scripts/measure-authoring-passes.fsx`, `evaluation/nyrec-scaling/profile.{py,fsx}` and `tools/zig/csharp-emitter/resource-parity.fsx`. All of them remain at the tag.
- **Kept, partly.** 39 test files tested both the F# libraries and the host. Only their host tests and the helpers those tests needed were removed. The tests of `Nyrwvy.Semantics`, `Nyrwvy.Execution`, `Nyrwvy.Backend.CSharp` and `Nyrwvy.Viewer` stay. `ZigBackendTests` keeps only its case-table test. `ZigSnapshotReaderTests` and `ZigLoweringTests` lose the tests that drove the host's Zig routes.
- **Moved.** `ZigTools`, with `BoundedOutput`, moved to `tests/Nyrwvy.Core.Tests/ZigTools.fs`. `ZigTools.enabled` was dropped.
- **Parity tests.** These tests now replay the recorded F# answers only.
  - The `live` and `record` modes are gone, with `NYRWVY_HOST_REFERENCE`, the record half of `scripts/host-golden.mjs` and the ten live-only tests.
  - The fixtures of the runPure, C# run and change-verification tests used to run on an in-process F# session. They now send the same requests to the Zig host (`publishWithZigHost`), under `NYRWVY_TEST_ID_SEED=fixture:<test>`, the seed the in-process fixtures used.
  - A test can build a second fixture. That fixture starts from an empty snapshot whose ID is the next seeded one, and the host reads that ID before it allocates. So the count continues as the in-process count did. The runPure compiler-failure test needs this.
  - The tests used to get the declarations from `session.Declarations()`. They now read them with the F# libraries (`declarationsOf`). The query builder (`queriesFrom`) reads the Zig host's `declarations` reply, which also has the provenance and covers resolved snapshots.
  - The ownership snapshot of `constructedSnapshots` is built in process with `AgentExpressionStructuralWorkflow`.
  - Replay compares only the first entry of a `PATH` override, the test's own directory. The C# worker test's recording holds the whole PATH of the machine that recorded it. Until this change, that test could pass in golden mode only in the same shell environment: it failed under `scripts/test-extended.fish` and would fail in CI. The step-4 extended runs never saw this, because they used the live F# host.
- **Other dependents.**
  - `Nyrwvy.ViewerExport` copies the input snapshot to a private directory and calls the Zig host's `viewFunction` there. It then writes the page with its own output checks. It no longer references any project.
  - CI no longer has the step for the legacy host selection.
  - `libraries/host/check/run.mjs`, `scripts/experimental/semantic-change-verification.py` and `examples/mini-work-planner/materialize.py` start `tools/zig/host/bin/nyrwvy-host`. `materialize.py` now takes a `.nyrec`, since `migrate` exists only at the tag.
  - The adapter no longer accepts `NYRWVY_HOST=fsharp`. `NYRWVY_HOST_EXECUTABLE` still accepts a `.dll`.
  - The extended settings no longer set `NYRWVY_ZIG_TOOLS`, which only the F# host read.
  - The READMEs build the Zig host instead of the F# host.

## Checks

| Suite | Before (`bf60381a`) | After |
| --- | --- | --- |
| Release build, `Nyrwvy.slnx` | 0 warnings | 0 warnings, 0 errors |
| Zig filter (`FullyQualifiedName~Nyrwvy.Core.Tests.Zig`), goldens | 164 | 137 passed |
| `scripts/test-fast.fish`, managed | 1,269 | 710 passed |
| `scripts/test-fast.fish`, Node `fast-contracts` | 64 | 64 passed |
| `scripts/test-extended.fish`, Core / end-to-end | 299 / 6 | 134 / 2 passed (7 min 21 s); 4 Node pipelines passed |
| partition fast / extended | 1,269 / 305 | 710 / 136 |

- The Zig filter lost 27 tests: the 10 live-only tests, 14 `ZigBackendTests` that ran runPure on the F# host, 2 `ZigSnapshotReaderTests` and 1 `ZigLoweringTests`. Every remaining parity test passed against the unchanged goldens, so the new fixtures send the recorded requests. No golden was re-recorded. The only removed run file is the one from the first part.
- `npm run test:host` passed. `node scripts/run-node-tests.mjs fast-adapters fast-mcp` passed 22 tests. `npm run test:bun` passed 3. `node libraries/host/check/run.mjs` passed all checks on the Zig host.
- `Nyrwvy.ViewerExport` was compared with the tool at `bf60381a`, built from `git archive` outside the worktree. Both wrote byte-identical pages for three functions of each of eight example snapshots, and the same default file name. They also wrote byte-identical pages with `--format json`, for three functions of an example converted to JSON. Both rejected an unknown function, and a record file passed as `--format json`. The error texts differ: they are now the Zig host's `code: message`.
- `rg 'Nyrwvy\.AuthoringSession'` outside `docs/` and `docs-archive/` finds three kinds of match:
  - The Zig host's usage texts in `tools/zig/host/startup.zig` and `bind_c.zig`. They are byte-compared with the recorded F# startup failures, so they stay.
  - The `tool` field of the committed `*.migration.json` reports, which record the past runs.
  - One dated measurement in `evaluation/zig-speed/README.md`.
- `rg 'NYRWVY_ZIG_\w*_CHECK'` finds nothing outside the decision and verification records.

## Not verified

- CI was not run. The edited workflow, `ci.yml`, was not executed.
- macOS and Windows were not run.
- The scripts rewired to the Zig host were not run: `semantic-change-verification.py` and `materialize.py`. Only their syntax was checked.
- Coverage lost with the host tests: language behavior that those tests checked through the F# host's runPure, on both backends. The Zig host parity tests and the Node adapter tests cover the Zig host. The F# C# backend keeps only its direct tests.
