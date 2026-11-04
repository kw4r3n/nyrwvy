# Zig runPure capability reachability

Base: `30c6262f`; Linux, Zig 0.17.0. This fixes pure entry execution in a workspace that also declares unsupported host capabilities. It does not implement native PTY or Ghostty bindings.

## Cause and change

`run_pure.zig` already retries unsupported whole-workspace emission using the entry's reachable functions. The projected IR retains capability declarations, however, and `fakeCapabilities` checked every declaration's operation types. An unrelated operation returning a capability therefore rejected a pure entry.

The emitter now records capability types while traversing emitted function values and named type fields. It follows operation types and named fields until neither adds another declaration, and validates and emits fakes only for the recorded capabilities. Required unsupported operations still fail; the sets and queues reset for each emission. All operations of a required capability remain checked.

## Verification

- Added a regression covering unused unsupported capabilities, directly required unsupported capabilities, capability references through product fields, named results used only by a capability operation, and state reset across consecutive requests. The regression fails against the base emitter and passes with the change.
- `zig test tools/zig/emitter/main.zig`: all 5 tests pass.
- `npm run test:host`: passes (runtime, model reader, both emitters, and all registered host tests).
- External diff review: no defects reported; `git diff --check` passes.
- Rebuilt the native host with `node scripts/build-zig-host.mjs`. Opened Rhifill `app/nyr/workspace.json` read-only and replayed `cases/layoutFrame-1-2-table.json` as `runPureByName` with `backend: "zig"`. Before: `RunBackendUnsupported` for capability operation types. After: `ok: true`, `matches: true`, with rows `[0,1,3,3,4,5]` and `[0,1,2,3,4,5]`. No Rhifill files were changed.
- Core execution IR and serializer tests: all 13 pass.
- The full `dotnet test tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj --no-restore` run aborts with a stack overflow in `SnapshotValidator.validateGraph`, during `SnapshotCodecTests.deep conditional snapshots roundtrip standalone and dependency resolved codecs`. It is not a passing full-suite result; the F# validator was not changed here.

No UI integration, latency benchmark, or long-running RSS measurement is included.
