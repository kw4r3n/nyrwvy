# Zig runPure preparation and generated layout cost (2026-11-05)

Base: `2b519f57`, Linux, Zig 0.17.0. Rhifill snapshot/function IDs are intentionally fixed in `bench.py`; this is a workload probe, not a general benchmark framework.

## Host improvement

`run_pure.runner` now emits the already-lowered entry directly, as the C# path does, instead of first lowering/emitting the whole workspace and retrying on unsupported constructs. No validation or unsupported-entry diagnostic was removed. The host was rebuilt locally.

Rhifill `app/nyr/cases/layoutFrame-1-2-table.json`, opened read-only, `backend: zig`; all results matched. Warm end-to-end measurements include host work, source/cache checks, process startup and result transport:

| Compiler invocation | Before | After (median of 10, first invocation excluded) |
| --- | ---: | ---: |
| PATH `zig` (mise shim) | ~90–92 ms | 42.50 ms |
| Absolute compiler executable | ~61 ms | 13.80 ms |

After samples (ms): shim 44.13, 42.11, 45.24, 42.07, 42.24, 41.29, 44.96, 41.31, 43.89, 42.75; absolute 14.28, 14.22, 14.43, 14.02, 13.62, 13.35, 13.41, 13.07, 13.09, 13.98. Each first invocation incurred a ~15 s compile; it is excluded, not hidden in the warm numbers. Before measurements were sequential earlier in the same investigation, not randomized interleaving.

`zig env` still runs on every request before the binary cache lookup: measured separately at ~32 ms via mise versus ~3.7 ms directly. No environment/tool settings were changed persistently. Blindly memoizing this result can miss changes to wrapper configuration or compiler selection; that needs a separate invalidation design.

Trade-off: workspaces that previously emitted successfully as a whole shared one compiled runner across different entries. They now compile each distinct entry on its first use. In exchange, unrelated function edits no longer change that entry's emitted source. Rhifill already used the per-entry fallback, so this trade-off does not add per-entry compiles to the measured case.

Validation: `npm run test:host` passed; real Rhifill saved case matched on all measured calls; `git diff --check` passed. Review identified the cache-sharing trade-off and benchmark patch/counting limits; these are addressed here and in the harness. The Core full suite was not rerun (the previous run aborted in the unchanged F# SnapshotValidator).

## Generated code: isolate execution from orchestration

Run `python evaluation/zig-layout-cost/bench.py /absolute/path/to/runner.zig` with the emitted Rhifill layoutFrame runner. Add `--count` for a separate instrumented run. The harness writes only a temporary copy, preserves generated functions, and substitutes a timing main. Source used here: SHA-256 `e30c95a4ba5c062f1278475fcec573b296681cd5e58e7e731e3b8fc5622c8a23`.

Input: all cell widths are 1, width table 1/2. Decode/build inputs once; retain borrowed inputs before each consuming call. Measure function execution plus result release, excluding input construction, checksum traversal, serialization, subprocess startup and compilation. Each of 3 processes warms 100 calls and averages 1,000 calls. Every result contributes to a checked checksum, and a separate final call checks every position and shape in every optimization mode. Checked mode also checks leaks. Timings below are medians of the three process averages, not p50/p99 of individual frames.

| Build / value allocator | 80×24 | 300×100 |
| --- | ---: | ---: |
| ReleaseSafe / current checked allocator + page cache | 11.28 µs | 130.77 µs |
| ReleaseSafe / smp allocator | 11.63 µs | 190.73 µs |
| ReleaseFast / smp allocator | 9.19 µs | 113.07 µs |

Removing the checked allocator alone **slowed down** this workload, especially at 300×100. ReleaseFast improved over current by ~19%/~14%, but removes safety checks and is not a like-for-like production replacement. No allocator or build-mode change was made.

Separate counters (not timing results):

| Input | nyAlloc calls/frame | Requested bytes through nyAlloc/frame | Box allocations/frame | realloc calls/frame |
| --- | ---: | ---: | ---: | ---: |
| 80×24 | 50 | 16,968 | 25 | 5 |
| 300×100 | 202 | 246,472 | 101 | 7 |

Boxes are included in nyAlloc calls. Requested bytes exclude realloc growth, allocator metadata and retained pages; these are not RSS or total allocated bytes. The harness counts both current direct realloc sites and fails if the patch targets drift. Allocation counts scale with rows, not individual cells here: per-cell heap allocation is not the dominant explanation for this case. The ~14×–12× time increase for ~15.6× as many cells gives no evidence of quadratic growth in these two samples; this is not an asymptotic proof.

The generated C# comparison is recorded below. Remaining investigations, if a real workload still loses: mixed wide/combining inputs, then CPU sampling to separate loop/ownership/allocator costs. These results do not measure P/Invoke, library-mode arena resets, UI rendering, p99 or long-running RSS. They do not establish that generated Zig is slower than generated C#.

## Generated C# comparison (same session)

`python evaluation/zig-layout-cost/csharp.py /absolute/path/to/rhifill/app/nyr/workspace.json` emits C# from the current host with an external-implementations profile (unused capabilities need declarations), without modifying the snapshot. Only the pure layoutFrame entry runs. A temporary .NET 10 Release project compiles that source without external packages. SDK: 10.0.403.

Both probes use the same two all-width-1 inputs, width table 1/2, 100 warm-up calls and 1,000 measured calls in each of three fresh processes; both validate positions and checksum. Compilation, input construction, result traversal and subprocess startup are outside the timed region. C# uses Stopwatch; Zig uses the monotonic awake clock. The C# benchmark lets GC run normally, includes pauses that occur inside timed calls, and reports allocation/collection counts. Zig includes explicit result release; C# has no matching deterministic release. GC may also occur outside the timed region, so these are function-cost comparisons, not complete steady-state frame budgets.

| Generated backend | 80×24 | 300×100 |
| --- | ---: | ---: |
| Zig ReleaseSafe / checked allocator, rerun | 10.92 µs | 131.23 µs |
| C# Release, tiered compilation disabled | 24.69 µs | 294.91 µs |
| C# Release, tiered compilation enabled | 166.89 µs | 278.37 µs |

Each value is the median of three process-average times. Disabling tiered compilation selects optimized JIT code immediately, avoiding the 100-call warm-up's failure to settle tier promotion for the short case. Tiered-on results are recorded to expose that sensitivity, not as a claim about fully warmed default .NET performance. Dynamic PGO was not separately stabilized. Zig is about 2.26×/2.25× faster than the tiered-off C# case here. This does not support a claim that the generated Zig layout is slower; the previously slower `runPure` API primarily spends time outside this function.

C# measured allocations per call: 58,072 bytes at 80×24, 829,448 bytes at 300×100 (tiered off); all 1,100 calls caused GC counts 3/0/0 and 54/1/0 respectively. C# GC-allocated bytes and Zig's instrumented nyAlloc requested bytes have different definitions; do not treat their ratio as a memory-use ratio. The C# persistent-list representation and its allocations are plausible contributors, but no CPU attribution was performed.

No P/Invoke/arena-mode/UI comparison, mixed-width input comparison, allocation-free C# rewrite, or per-frame tail-latency claim is included. Runtime source and production settings remain unchanged by these probes.

### Additional JIT warm-up check

Review flagged that 100 calls do not settle .NET tier promotion. `csharp.py` now adds one second of unmeasured execution before the same 100 + 1,000 call sequence. Zig has no JIT and the table above uses its measured 100-call warm-up. This deliberately gives C# more warm-up; it still does not prove all dynamic PGO phases have settled.

| Backend / warm-up | 80×24 | 300×100 |
| --- | ---: | ---: |
| Zig checked, 100 calls | 10.92 µs | 131.23 µs |
| C# tiered on, 1 second + 100 calls | 17.48 µs | 274.01 µs |
| C# tiered off, 1 second + 100 calls | 18.71 µs | 303.88 µs |

Thus the cautious headline is that Zig was ~1.6×/~2.1× faster than warmed tiered-on C# **in this workload and timing method**, not the much larger ratio from short tiered-on warm-up. C# GC still uses workstation defaults. The mismatch between deterministic Zig release and C# GC accounting remains explicitly bounded as above.

Provenance: current snapshot bytes SHA-256 `a52fb69da2a8cfa9f5cc9662ba0cd68576b666832e026112ca6a190b38eb5310`; freshly emitted C# source `ef056238fc51a5caf674771d9cbc530df99e7a82b7795f9fe8739259c16bcfc1`. The current host's newly cached Zig runners were checked and have the same source hash `e30c95a4…` as the benchmark input. The width-table constructor and Zig decoder both use stable field-ID order. The standalone scripts print C# provenance but do not enforce cross-run snapshot pinning: reruns must verify provenance again.


## Shared-library experiment artifacts

`library-findings.md` records the dated table-path investigation and accepted
constructor-storage reuse, with raw results in `library-probe-results.txt` and
`optimization-{comparison,profile,tests}.txt`. These are historical measurements,
not generated golden tests or portable performance thresholds. Timing and counter
runs are separate; instrumentation is confined to scratch generated source.

Use `library_probe.py layout.zig liblayout.so workspace.json` for pattern/counter
measurements and `compare_libraries.py original.so candidate.so [...]` for parity
and native ABI timings. Build all compared libraries with the same Zig compiler
and ReleaseSafe settings, and run serially with no concurrent build.

`state_update_experiment.py` and `segment_width_experiment.py` are experimental
authoring scripts, not migrations to run against current projects. They require
the original Rhifill layout snapshot `01a181cb-b5af-74ea-99e0-6f65dfcb99f7` and its
dependency sidecars (before cached widths removed `mergeTargets`/`alignRow`), plus
a current `npm run build:host`. Both take the snapshot path and a new output
directory **outside** the input snapshot directory, copy inputs first, and mutate
only copies through simulate/commit. The width script is not idempotent on an
already-rewritten graph. Example: `python evaluation/zig-layout-cost/segment_width_experiment.py /path/to/original/workspace.json /tmp/nyrwvy-width-trial`.

The constructor optimization is in the current emitter. Recreating the original
versus optimized compiler comparison requires separate checkouts of the parent
emitter and this change; merely running both graph variants with the current host
does not recreate a no-constructor-reuse baseline. The rejected later unchanged-field
optimization is not part of this directory or the accepted emitter.

## Kind A: a hand-written `layoutFrame` through a mapping table (2026-11-06)

Question: is a `"functions"` table entry ([plan §10](../../docs/plans/multi-backend-and-bindings.md)) worth it for Rhifill's `layoutFrame`? Reproduce with `kind-a/` (`build.mjs` builds two libraries from a scratch copy of `app/nyr`, `check_bench.py` checks and times them; raw output in `kind-a/results.txt`). Base `ffaec3b4`, Zig 0.17.0, ReleaseSafe, host built from the same commit. The Rhifill repository was only read. The retained copy in `workspace.json.dependency.json` was enough; the sidecar's stale `host.json` path was not needed.

**Finding 1: `layoutFrame` itself cannot be bound.** Its second parameter is the `CellGrid` Product, and a host declaration takes no Product argument (`hostValue(..., argument = true)` refuses it). `emitZigLibrary` rejects the table with `The Zig library mode does not yet support: host binding parameter type Named (StableId "01a17734-…", [])`. The probe therefore adds a scratch-only wrapper `layoutFrameFlat(rows, narrow, wide) = layoutFrame(rows, CellGrid{narrow, wide})` and binds that. A real use would need the same wrapper in the Rhifill graph (and a changed export), so this is a cost of kind A, not of the probe.

**Declaration** (`kind-a/layoutframe.zig`, 95 lines, returns `error{OutOfMemory}![]const []const i64`): written from the Nyr graph (segment widths between code-3 separators, column-wise max over a block's rows that have the first row's segment count, alignment of separators, running positions for other rows, wrapping adds). Nothing in the compiler or the Rhifill graph was changed.

**Agreement.** All 8 saved `layoutFrame-*.json` cases give the expected positions on the Nyr body (both `ny_layoutFrame` and the wrapper) and on the declaration. A further 18,029 comparisons are identical on all three: seeded random frames (empty rows, separator densities 0 to 100 %, unequal segment counts, odd codes, grids including `(0,0)`, `(-1,4)` and `2^62` for wrap-around) and the eight timing frames. This is parity with the Nyr body, not an independent oracle. `runPure` was not used (it always runs the Nyr body).

**Timing.** Through the C export with ctypes: frame buffers built outside the timed region, 100 warm-up calls, 150 to 3,000 individual calls per case, a fresh process per run, median over 5 processes of each process's p50 and p99, in µs. Codes: `narrow` is all 1; `wide` repeats `1,2,0,1,1,2,0,1`; `frame` is that wide text with one five-row table block in every twenty rows; `table` repeats `1,2,0,1,3,1,1,1` in every row. Grid is 2:3. ctypes adds roughly 1 µs per call. A is `ny_layoutFrame` (Nyr body), B the wrapper (Nyr body, showing the wrapper costs nothing), C the declaration.

| case | A p50 / p99 | B p50 / p99 | C p50 / p99 | A / C (p50) |
| --- | ---: | ---: | ---: | ---: |
| 80×24 narrow | 11.1 / 15.1 | 11.2 / 13.4 | 6.3 / 8.7 | 1.8× |
| 80×24 wide | 12.3 / 15.8 | 12.4 / 15.8 | 6.7 / 9.0 | 1.8× |
| 80×24 frame | 23.3 / 26.1 | 23.4 / 26.2 | 7.8 / 9.8 | 3.0× |
| 80×24 table | 62.9 / 88.1 | 63.0 / 67.2 | 10.3 / 13.0 | 6.1× |
| 300×100 narrow | 121.2 / 158.6 | 121.8 / 160.2 | 58.8 / 117.7 | 2.1× |
| 300×100 wide | 152.8 / 189.9 | 153.9 / 188.6 | 62.8 / 122.8 | 2.4× |
| 300×100 frame | 309.2 / 368.1 | 312.5 / 358.5 | 76.7 / 127.8 | 4.0× |
| 300×100 table | 641.3 / 693.9 | 641.9 / 686.5 | 100.3 / 146.2 | 6.4× |

The narrow rows agree with the runner numbers above (11 µs, 131 µs). C keeps a floor of about 6 µs and 59 µs: it still copies the input rows into counted values, the declaration's result into counted storage, and that into the caller's buffers. How the floor splits was not measured.

**Frame budget.** 60 Hz is 16.7 ms (Rhifill's own budget is 6.25 ms at 160 Hz). The native pane (`spikes/native-pane/src/main.zig`, `layout` at line 259, called at line 992) lays out one whole pane per drawn frame, only when `host_frame_dirty` reports changed cells or the geometry key changed; it does not lay out per row, and there are up to eight panes. The worst measured Nyr-body case, a 300×100 pane made only of table rows, takes 0.64 ms, 3.8 % of 16.7 ms. The `frame` mix takes 0.31 ms (1.9 %) and 80×24 takes 12 to 23 µs (0.1 %). Kind A saves 0.23 ms in the 300×100 `frame` case (1.4 % of a 60 Hz frame) and 15 µs at 80×24. Eight dirty 300×100 `frame` panes in one frame would be about 2.5 ms on the Nyr body and 0.6 ms on the declaration; that sum is arithmetic from one pane's number, not a measurement.

**Verdict: not a first candidate.** Kind A works on this function and is large in relative terms (2× to 6×), but the absolute saving is at most about 0.5 ms for one large table-heavy pane, under 3 % of a 60 Hz frame. The price is a second 95-line implementation kept in step with the graph by hand, a wrapper function and export change in the Rhifill graph, and a table only the Zig backend honors. Rule of thumb for a first candidate: a pure function whose Nyr-body time in its real workload exceeds about 10 % of the frame (about 1.7 ms at 60 Hz) after graph-level fixes (the cached-width change already took the table path from about 2.4 ms to 0.64 ms), whose arguments are already lists and scalars, and whose cost is not dominated by copying at the boundary. Revisit `layoutFrame` if several large table-heavy panes are dirty in the same frame.

Not checked: the C# P/Invoke path, p99 under CPU load or with other panes drawing, one-hour RSS, arena capacity growth, ReleaseFast, other machines, and how the floor splits between input copy, declaration and output copy.
