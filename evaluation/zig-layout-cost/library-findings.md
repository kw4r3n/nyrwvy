# Shared-library layout investigation — 2026-11-05

This dated experiment log supersedes any inference from the all-narrow `layoutFrame` benchmark about mixed/table input. The initial investigation did not modify production sources. The follow-up below changes the Nyrwvy emitter; Rhifill's working changes remain untouched. Read Rhifill's current `app/Screen.cs`, `tests/layout/Program.cs`, and `docs/zig-layout-results.md`; the ABI source used is SHA-256 `38b81ca1bad4b27c2d1fd25d4a33f30bacb39798f477040e54cafb31f6fcae25`.

## Method and observations

`library_probe.py layout.zig liblayout.so workspace.json` preallocates flat input/output buffers and calls the actual library serially through ctypes, with 20 warm-up and 100 measured calls per case. All rows use the same repeating pattern: narrow `[1]`, wide `[1,2,0,1,1,2,0,1]`, separators `[1,1,1,1,3,1,1,1]`, mixed `[1,2,0,1,3,1,1,1]`. These are controlled patterns, not the Rhifill seeded-random UI benchmark. The measured time includes ABI input copying, native computation, output copying, arena reset, uncontended locking and ctypes overhead; it excludes C# flattening/reconstruction. No subtraction estimates P/Invoke cost.

| Pattern | 80×24 p50 | 300×100 p50 | 600×100 p50 | 300×200 p50 |
| --- | ---: | ---: | ---: | ---: |
| narrow | 11.47 µs | 121.11 µs | 230.64 µs | 242.12 µs |
| wide | 11.80 µs | 127.79 µs | 326.36 µs | 250.97 µs |
| separators | 163.79 µs | 2384.86 µs | 4716.94 µs | 4755.49 µs |
| mixed | 162.81 µs | 2366.50 µs | 4666.43 µs | 4728.59 µs |

The expensive condition is entering the table-alignment path on code 3, not wide glyphs alone. Doubling columns or rows approximately doubles the expensive cost here; there is no evidence of quadratic scaling in these samples.

A separate temporary instrumented library wraps generated functions, preserving signatures/ownership, and counts allocations and reference-count operations against the innermost active generated function. It checks every output against the original library for all four 300×100 patterns. Its timing is NOT used above: instrumentation changes optimization. Source edits are confined to a temporary copy; the actual .so is never overwritten. Full counters and sequential arena capacity observations are in `library-probe-results.txt`.

## Ranked findings

### 1. Per-cell state boxes on the table path — strongest optimization candidate

At 300×100 with separators or mixed input:

- `segStep`: 60,000 calls, 60,000 boxes, 61,400 nyAlloc calls, 2,614,400 requested bytes.
- `alignStep`: 30,000 calls, 30,000 boxes, 30,200 nyAlloc calls, 1,688,800 requested bytes, 800 realloc calls.
- Both use `productConstruct` in the semantic graph to recreate fold state. `tools/zig/emitter/main.zig:1186` always lowers that to `nyBox`. Only explicit `productUpdate` currently uses the count-of-one in-place path (`:1188`).

This confirms per-cell allocation on the expensive path, whereas the earlier all-narrow experiment did not take this path. It does not isolate what percentage of CPU time boxing costs. First experiment: express equivalent state updates with ProductUpdate in an isolated graph copy and verify aliasing/parity; assess whether Nyr should infer constructor reuse or scalar-replace nonescaping state. Do not remove reference counting to obtain the result.

### 2. The same segment widths are computed twice — confirmed redundant work candidate

`segmentWidths` executes 200 times for 100 rows; its `segStep` fold visits 60,000 cells. `blockTargets`/`mergeTargets` first compute widths for target selection; `alignRow` computes them again. `cellUnits` executes 78,900 times and `isSep` 90,500 times for only 30,000 cells. The exact graph may need both phases, but carrying the computed segment widths between phases could remove one fold. Owner: Rhifill layout graph initially; Nyr-level CSE across calls needs purity/lifetime analysis and is a larger change.

### 3. Retain/drop traffic in the inner loops — confirmed volume, CPU share unmeasured

`segStep` accounts for 344,200 retains and 352,800 drops; `alignStep` for 277,300 retains and 311,100 drops. `cellUnits` alone drops a passed capability-free grid value 78,900 times. These are instrumented calls to the header operations, not assembly instruction counts. Borrow analysis, eliminating redundant field reads/retains, and state reuse are candidates. Assembly/CPU sampling or a controlled counterfactual is needed before ranking this above boxing.

### 4. C# representation conversion and duplicate outputs — confirmed extra work

`app/Screen.cs:61` creates a new flat input, row lengths, flat output and output lengths on every frame. `nyRows` copies inputs again into native counted rows; `nyCopyRows` copies results out. Then RunZig copies the flat output into `long[][]`, builds each `NyList<long>`, and builds the outer NyList. Both `Xs` and `XsList` survive in the frame. `app/Program.cs:398` uses XsList for pointer hit-testing (`columnAt`).

Candidates: frame-sized scratch-buffer reuse with correct resize/lifetime handling; defer hit-test representation creation until needed; reduce duplicate representations at a deliberate boundary. However, the ~2.37 ms native call above already excludes these managed conversions, so this alone cannot fix the measured table-path cost. Bytes crossing the boundary are not a complete allocation accounting.

### 5. Library allocator behavior differs from the runner — confirmed difference, cause unresolved

Library mode leaves `ny_values` null and uses the arena; runner ReleaseSafe uses DebugAllocator with a page cache. `nyFree`/realloc behavior therefore differs. Arena reset retains capacity and runs at export exit. In the sequential sweep retained capacity rose from 51,812 bytes to 1,617,524 at 300×100 mixed, then 4,045,406 for larger input; it stayed at that high-water mark for following cases. This is expected retained capacity, not evidence of a leak or RSS stability. Requested allocation bytes are cumulative calls, not live bytes. No evidence justifies swapping the allocator yet.

### 6. Safety checks / code generation — secondary candidate

Rhifill's recorded ReleaseFast diagnostic reduced integrated latency ~27–30% but still lost to C#. It changed compiler safety/optimization policy and must not be adopted as the fix without deciding semantics. Our all-narrow comparison likewise cannot be extrapolated to table-path loops. Product boxes, field-sharing expressions and callback boundaries are specific targets for inspecting optimized code; the many emitted temporary variables themselves are not evidence of machine-code overhead.

### 7. Locking, resize retry, and compilation — not explanatory for the serial measurements

Exports serialize through one global mutex. Contention can matter with multiple simultaneous callers, but this probe is single-threaded. Correctly sized buffers returned status 0 every time, so the wrapper's status-3 retry was not active. Compilation/runPure/source emission do not execute inside a prebuilt shared-library call. Emitting unused workspace functions increases generation/build size but is not established runtime overhead.

### 8. Whole-frame layout on every screen read — confirmed policy, wasted frequency unmeasured

`Screen.cs:267` unconditionally lays out every row. `Program.cs:328` calls Read for display updates; scroll/selection refreshes also call it. Changes only to colors, cursor or selection need not change the width codes, but there is no layout reuse at this boundary. A cache keyed by width-code content and grid could avoid redundant work; measure unchanged-code frequency first. Incremental row invalidation alone is unsafe because table alignment depends on other rows in the contiguous block. Scroll genuinely changes visible content, so those reads must not be classified as redundant without checking.

## Suggested order

1. Isolated graph experiment for state reuse and retaining computed segment widths, with mixed-width/table parity and output lifetime tests.
2. Profile generated machine code after that change; borrow/retain simplification only with ownership evidence.
3. Reduce Rhifill's conversion/allocation costs independently and remeasure the full managed boundary.
4. Stress multiple exports, variable frame sizes and long-running RSS only after correctness and representative latency pass.

No production fix, P/Invoke no-op baseline, CPU-time attribution, mixed-input C# function-only comparison, concurrent contention measurement, or one-hour RSS run is claimed here. These observations are for one machine and these fixed patterns, not universal backend rankings.

Review follow-up: the probe now reports non-truncated per-frame averages and poisons/checks the output on every instrumented invocation. The rerun passed all 84 full-output checks (21 calls × 4 patterns); all counter averages exactly matched the recorded initial counters, with no fractional remainder. Header retain/drop counts include calls that may be no-ops for static values, not measured optimized-machine-code instructions.


## Follow-up: constructor reuse and cached widths

Implemented bounded constructor-to-ProductUpdate lowering in the Zig emitter (see
`docs/decisions/zig-product-constructor-reuse.md`). Graph signatures and persisted graphs
stay unchanged. Separately, `segment_width_experiment.py` creates an isolated graph
through the public simulate/review/commit API: prepared rows carry cells and segment
widths so target selection and alignment share one width computation. This graph change
was not applied to Rhifill production during this isolated experiment; see the later integration note below.

The same-run comparison below uses ReleaseSafe shared libraries, 100 warm-ups and 500
measured native ABI calls per case. Buffers are preallocated; Python output conversion
is outside timing. Rows rotate the fixed input pattern by row index; grid widths are
2 and 3. This differs from the initial probe above, so compare candidates within this
table, not across the two experiments.

| Variant | mixed 80×24 p50 | mixed 300×100 p50 | mixed 300×100 p99 |
| --- | ---: | ---: | ---: |
| Original | 163.62 µs | 1977.51 µs | 2138.81 µs |
| Constructor reuse | 103.28 µs | 1259.73 µs | 1293.19 µs |
| Cached widths | 103.39 µs | 1086.68 µs | 1155.30 µs |
| Combined | 62.61 µs | 639.18 µs | 707.21 µs |

The combined 300×100 mixed p50 is 67.7% lower (3.09× throughput at this call boundary).
Narrow 300×100 p50 was 123.54 / 113.94 / 123.39 / 121.64 µs respectively;
wide was 153.27 / 113.86 / 123.32 / 120.67 µs. These are one-machine observations,
with variants measured sequentially, not a managed end-to-end or C# comparison.

`compare_libraries.py` passed 3,000 candidate-to-original comparisons: 250 inputs ×
4 grids × 3 candidates. Inputs cover empty rows, separators, unequal rows, interrupted
table blocks and deterministic random data. It compares full outputs, insufficient-buffer
responses and unchanged input cells. This is parity with the original, not an independent
layout specification oracle. The emitter test suite passed 7/7, including executable
unique/shared/nested/error-path ownership tests under DebugAllocator. The broader Zig
test script passed as recorded in `optimization-tests.txt`.

An instrumented emitter-only run confirms segStep boxes drop from 60,000 to zero and
alignStep boxes from 30,000 to zero per 300×100 table frame. Retain traffic remains;
this change is not borrow elimination. Counter timing is not used for speed claims.

Raw same-run comparison and emitter-only profile are saved alongside this report.
No allocator replacement, global CSE, runtime pool, or ReleaseFast switch was needed.
At this experiment stage, Rhifill managed integration, concurrent contention, long-running RSS
and an independent C# mixed-input comparison were unverified. Deferring a source release to its constructor
can increase temporary live memory.

External review found no ownership defect, but identified quadratic backward-search
cost on long blocks. The final rewrite caps search at 64 preceding instructions and
adds a beyond-window regression. Timings above precede this cap; the cap only limits
optimization eligibility, and final performance has not been remeasured.

Final validation: `npm run test:host` exited 0 after the search-window regression was
added. `zig fmt --check`, `git diff --check`, and Python syntax checks passed.


## Later integration and scope of this commit

The accepted Nyr change is only the bounded constructor-storage reuse pass and its
regressions. The separate unchanged-field retain/drop experiment was rejected and is
not included. No borrowed calling convention, allocator change or ReleaseFast switch
is included. The final emitter source tested here has SHA-256
`5352973f5964fb4c9eab095fcc60c264cd4ce10252a6f5f060cd75c47d62a6be`.

Rhifill subsequently applied the cached-width graph through its authoring API and
validated the rebuilt final emitter, then separately pooled managed scratch buffers.
Those changes and their end-to-end measurements belong to Rhifill's
`docs/zig-layout-results.md`, not this Nyr implementation. Its final integration
checks passed 5612 layout comparisons and 6 bitmap comparisons; those results do not
isolate this compiler pass's CPU share. The historical native table above remains
explicitly pre-search-cap data. Long-running RSS was not measured.
