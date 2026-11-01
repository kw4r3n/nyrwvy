# F# host goldens for the Zig host parity tests

Step 4 of the switch in [zig-session-host.md](../decisions/zig-session-host.md#the-switch): the F# host's answers to the parity corpora are recorded in `tests/goldens/zig-host/`, and the Zig host parity tests can run against them without an F# host. Working tree on `feat/fsharp-goldens` at `a3f3b51`, Linux.

## Reference

- `git diff fsharp-host-final..HEAD -- tools/Nyrwvy.AuthoringSession` is empty, so the goldens are the tagged F# host's answers. Under `src/`, the only change is the test-only `StableIdAllocator.withTestSeed`, which no host calls.
- Recorded with `NYRWVY_HOST_REFERENCE=record` and the extended check variables (`NYRWVY_ZIG_EMITTER_CHECK`, `NYRWVY_ZIG_READER_CHECK`, `NYRWVY_ZIG_LOWERING_CHECK`), filter `FullyQualifiedName~Nyrwvy.Core.Tests.Zig&Suite!=Benchmark`: 164 passed, about 7 min.
- Size: 962 runs of 110 tests, 625 file blobs, 1,587 files, 11.9 MB (Brotli; about 150 MB uncompressed).

## Checks

- Determinism: two recordings, compared with `diff -r`, differ only in the two unseeded-allocation tests (`Unseeded declaration reviews …`, `Unseeded operation reviews …`), whose F# IDs are random by design and masked by their tests.
- Golden mode (`NYRWVY_HOST_REFERENCE=golden`), same filter: 164 passed in 5 min 43 s (the live-only tests return at once).
- Live mode (the default while the F# host is built) is covered by the extended suite below.
- Tampering: one byte of a recorded reply (`"ok":tru3`) and one byte of a recorded `snapshot.json` blob in `Commit and reject persist identical files …` each make that test fail in golden mode (JSON parse failure; `File differs: snapshot.json at byte 2`). Unchanged goldens pass it.
- Not run: the default switching to golden when `Nyrwvy.AuthoringSession.dll` is missing. The test assembly still loads that DLL in process, so it cannot be hidden before step 5.
- Release build: no warnings or errors.
- `scripts/test-fast.fish`: 1,269 managed passed; 63 Node contracts passed (after `npm ci --ignore-scripts` in this fresh worktree, which had no `node_modules`).
- `DOTNET_PROCESSOR_COUNT=2 scripts/test-extended.fish`: 299 Core and 6 end-to-end passed (19 min 46 s), partition 1,269 fast / 305 extended, 4 Node pipelines passed.

## Not golden

Live F# only (they return early unless `live`): the two lock tests, the source-edit-during-execution test, the F# seed test, and six dependency-upgrade tests whose review fingerprints bind the absolute workspace path. The tool-level comparisons against `Nyrwvy.Semantics`/`Nyrwvy.Execution` (validator, lowering, snapshot reader, model reader, fingerprints, typed writer) and `ZigBackendTests` stay live; see the decision document for why.
