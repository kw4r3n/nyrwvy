# Removal of the Zig parity checks and the F# emitter

Step 5 of the switch in [zig-session-host.md](../decisions/zig-session-host.md#the-switch), first part: `NYRWVY_ZIG_EMITTER_CHECK`, `NYRWVY_ZIG_READER_CHECK`, `NYRWVY_ZIG_LOWERING_CHECK` and the F# emitter (`src/Nyrwvy.Backend.Zig`) are removed. That unblocks reference counting ([memory ownership](../decisions/zig-backend-memory-ownership.md)). Branch `feat/fsharp-removal` at `7ce6f26a`, from main `ab5d0db6`, Linux.

## Change

- The F# host's runPure Zig backend always lowers with `model-reader` and emits with the Zig emitter. Where `model-reader` reports an error or cannot run, F# lowers instead, as before. Before this change, the default path without `NYRWVY_ZIG_TOOLS` used the F# lowering and `ZigBackend.runner`.
- `tests/extended.runsettings` sets `NYRWVY_ZIG_TOOLS=1` in place of the three check variables, so the extended suite still runs the Zig routes, without comparing them to F#.
- `ZigRuntime`, the generator for `case_mapping.zig` and `case_runs.zig`, moved to the test project, together with its staleness test. `fsbench` no longer has `emit`. `ZigSpeed` no longer has an F# emitter row.
- The extended test `The Zig emitter matches the F# emitter on every repository snapshot` became `The Zig emitter emits every repository snapshot and caches a repeated emission`.
- `native Zig host preserves multiline unsupported diagnostics through the checked emitter tool` no longer runs its `NYRWVY_ZIG_EMITTER_CHECK` case. Its third run file, the last F# start in that test, was deleted. The test keeps its name, because the run files are keyed by it. No golden was re-recorded.

## Checks

- Release build of `Nyrwvy.slnx` and of `evaluation/zig-speed/fsbench`: 0 warnings, 0 errors.
- Zig filter in golden mode (`NYRWVY_HOST_REFERENCE=golden NYRWVY_ZIG_TOOLS=1`, `FullyQualifiedName~Nyrwvy.Core.Tests.Zig&Suite!=Benchmark`): 164 passed (step 4: 164).
- `scripts/test-fast.fish`: 1,269 managed tests passed (step 4: 1,269). The 64 Node `fast-contracts` tests passed.
- `npm run test:host`: passed.
- `DOTNET_PROCESSOR_COUNT=2 scripts/test-extended.fish`: 299 Core tests and 6 end-to-end tests passed, in 19 min. The partition is 1,269 fast and 305 extended. The 4 Node pipelines passed. All counts match step 4. This run used the live F# host, so it exercised the new runPure route.
- `rg 'NYRWVY_ZIG_\w*_CHECK'` outside `docs-archive` and the goldens matches only decision and verification text that records the history. No code, test, settings or script matches it.

## Not done

- The F# host project (`tools/Nyrwvy.AuthoringSession`) and the rest of step 5 still exist. About 129 test files, some 650 tests, test the host in process. `tools/Nyrwvy.ViewerExport`, CI (`.github/workflows/ci.yml`) and `libraries/host/check/run.mjs` also depend on it. Whether to delete those tests with the host is the owner's decision.
- `Nyrwvy.Semantics` and `Nyrwvy.Execution` stay. `Nyrwvy.Backend.CSharp`, `Nyrwvy.Viewer`, the test projects and the F# host all reference them.
