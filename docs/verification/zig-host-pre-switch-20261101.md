# Zig host pre-switch hardening

> Merge note (2026-11-01): merging origin/main (PR #590, #591) replaced `NYRWVY_AUTHORING_EXECUTABLE` with `NYRWVY_HOST=zig` and the test-only `NYRWVY_HOST_EXECUTABLE`. The commands below are recorded as they were run; today use `NYRWVY_HOST=zig NYRWVY_HOST_EXECUTABLE=/absolute/path/to/host` for the same run.


Scope: startup CLI parity, session memory retention, and build identity. The F# host remains the default; full typed-model reads and the default switch are a separate stage. See the [decision amendment](../decisions/zig-session-host.md#pre-switch-hardening-2026-11-01).

Observed on the working-tree changes based on `7508977`, Linux, Zig 0.17.0, .NET SDK 10.0.403/runtime 10.0.14, Node 26.12.1, and Bun 1.4.4. This run did not repeat the checks on other operating systems or the documented older Node/Bun pins.

## Changes

- Native `create`, `pin`, and single/batch/selection `upgrade` reuse dependency planning, exact-source checks, locking, and atomic publication. Creation never replaces an existing destination. Relative dependency inputs become absolute before comparison and persistence. Pin prepares its new embedded selection before writing it.
- Commit/reject rebuild the live typed generations and interner before publication, preserving shared declarations and bodies. A failed preparation or persistence attempt leaves the prior state usable; successful installation does not allocate. Failed proposal encoding restores newly interned entries. The current payload and latest dependency payload remain owned independently, so ordinary commits cannot invalidate the selected dependency.
- Host builds embed the compilation-time Git commit and include it in the cache key. A runtime Git change cannot relabel the executable. `NYRWVY_AUTHORING_EXECUTABLE` selects an explicit host in the shared adapter; omission keeps the F# default. Mock transport tests isolate this setting so they still exercise their controlled children.

## Verification

- Release solution build: zero warnings/errors.
- Fast Core tests: 1,269 passed, zero failures/skips.
- Maintained .NET extended suite with reader/lowering/emitter comparison flags: Core 285 and Extended 6 passed (291 total), zero failures/skips. Core finished in 11.66 minutes including cold native tool builds. This includes final relative-path startup cases, seeded pin recovery, commit/reject/dependency lifecycle comparisons, and build identity under a different runtime Git response.
- Adapter/schema/typechecking contracts: 10 passed. Both adapter and Bun TypeScript configurations passed.
- Bun adapter unit tests: 3 passed, zero failures.
- Fast Node transport/script and MCP contracts through the Zig host: 63 passed, zero failures/skips. This includes the real shared adapter; the transport fault fixtures intentionally keep their controlled child processes.
- The same fast Node contracts through the default F# host: 63 passed. Node extended generation/execution/relocation pipelines through the Zig host: 4 passed. Both runs had zero failures/skips.
- Mock fixture CLI and MCP shutdown contracts, with an unrelated executable override present: 16 passed, zero failures/skips.
- Native typed-model self-checks: 4 passed, including allocation-failure enumeration during live-set preparation.
- Native lifecycle self-check: 200 same-function commits, 200 rejections, 200 encoder rollbacks, and 200 cancelled preparations while retaining a stale proposal; the dependency payload is replaced midway. Retained arena capacity plus interner requested bytes stayed between 77,368 and 77,404 bytes. The ReleaseSafe process took approximately 0.070 seconds with peak RSS 12,236 KiB on this machine. This is a tiny fixture microcheck, not a real-workspace performance claim; the retained-byte counter excludes the fixed startup baseline, allocator bookkeeping/slab rounding, and request scratch.
- Existing-sample session check: copy `examples/task-cancellation-outcome/fixtures/task-outcomes.nyrec` (62,348 bytes) and its sidecars into a temporary directory; create one Unit-returning function, retain one stale replacement proposal, then replace/commit that function 100 times. Read/reject the stale proposal afterward and verify zero pending proposals. All operations succeeded; the original file's SHA-256 remained unchanged. RSS sampled every ten rounds ranged from 29,672 to 34,724 KiB; both round 10 and round 100 were 29,672 KiB. Median commit time was 23.68 ms while the extended suite was running concurrently; this is an observation, not an isolated performance comparison.

Suite discovery found 1,269 fast and 291 extended tests, with no overlap or missing ownership. `git diff --check` passed.

Main verification commands:

```sh
dotnet build Nyrwvy.slnx -c Release --no-restore -m:1
dotnet test tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj -c Release --no-build --no-restore --settings tests/fast.runsettings
dotnet test Nyrwvy.slnx -c Release --no-build --no-restore --settings tests/extended.runsettings
npm run typecheck
npm run test:adapters
npm run test:bun
NYRWVY_AUTHORING_EXECUTABLE= npm run test:node
NYRWVY_AUTHORING_EXECUTABLE=/absolute/path/to/built/host npm run test:node
NYRWVY_AUTHORING_EXECUTABLE=/absolute/path/to/built/host npm run test:node:extended
node scripts/check-test-suite-partition.mjs
```

The native adapter runs used the host produced by `ZigTools.tool "host"` from these sources. The default host was not changed. Process-based Node tests and VSTest required execution outside the sandbox; inside it, child stdin closed prematurely or VSTest's local socket was denied.

## Reproduce the memory self-checks

Run from the repository root with the pinned Zig. The generated module below is a test-only build identity; production host builds receive compilation-time metadata from `ZigTools`.

```sh
zig test -O Debug \
  --cache-dir /tmp/nyrwvy-memory-zig-cache \
  --global-cache-dir /tmp/nyrwvy-memory-zig-global \
  --dep snapshot_reader -Mroot=tools/zig/model-reader/typed.zig \
  -O Debug -Msnapshot_reader=tools/zig/snapshot-reader/main.zig

printf 'pub const value = "nyrwvy-zig-host-test";\n' > /tmp/nyrwvy-memory-build-info.zig
zig test -O ReleaseSafe \
  --cache-dir /tmp/nyrwvy-memory-zig-cache \
  --global-cache-dir /tmp/nyrwvy-memory-zig-global \
  --dep model_reader --dep snapshot_reader --dep zig_emitter \
  --dep csharp_emitter --dep viewer_asset --dep build_info \
  -Mroot=tools/zig/host/proposals.zig \
  -O ReleaseSafe -Mmodel_reader=tools/zig/model-reader/main.zig \
  -O ReleaseSafe -Msnapshot_reader=tools/zig/snapshot-reader/main.zig \
  -O ReleaseSafe -Mzig_emitter=tools/zig/emitter/main.zig \
  -O ReleaseSafe -Mcsharp_emitter=tools/zig/csharp-emitter/root.zig \
  -O ReleaseSafe -Mviewer_asset=src/Nyrwvy.Viewer/asset.zig \
  -Mbuild_info=/tmp/nyrwvy-memory-build-info.zig \
  --test-filter 'live-set replacement'
```

## Limits

The startup baseline still lives until process exit, and current/pending JSON projections plus the latest selected dependency payload remain. The change removes accumulation of committed history; it does not complete the typed-read migration. Rebuilding live typed state adds work on commit/reject. Legacy migration remains on the F# host, and C# execution still requires .NET.
