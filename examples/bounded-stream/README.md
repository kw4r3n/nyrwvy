# Moving-cursor capture-retention regression

Base: `90e495c75dd6915440ea22c96222aefcf04f723e` (main after PR #375).

Two ordinary graph tasks advance OneShot cursors, then remain blocked at a host
I/O checkpoint. The harness requires their discarded initial heads to collect
while both workers are live and their owner remains open. Pending-cell registry
size stays at two. Rooted/unrooted GC controls and an after-settlement/closure
control keep these lifetime observations separate.

## Verified result — 2026-10-11

The original emitter retained all 4/4 and 256/256 completed links. The repaired
emitter passes both cases under the default tiered JIT: initial heads collect,
workers remain live, the owner remains open, and its registry contains two tails.
The same saved six-function, 81-node graph was reemitted. Generated C# was not
edited. Graph fixtures, public signatures and the symbol manifest were unchanged.

Local verification reported:

- Clean Release build
- Source/compiler-helper checks: 5/5
- Reopened graph replay: 2/2
- Default-JIT live/open-owner collection: 2/2, all GC and settlement controls
- TaskScope tests, including their Extended cases: 7/7
- Maintained fast core suite: 851/851; maintained Node contracts: 32/32

These results were executed by the local .NET verifier, not the cloud executor.
The complete Extended suite and a broader JIT/platform matrix were not run.
See [repair verification](../../docs/verification/task-start-capture-release-20261011.md)
for the failed first candidate, implementation, evidence identity and limits.

## Run

With the repaired emitter and the repository's normal .NET 10/Node setup:

```sh
node examples/bounded-stream/run.mjs /path/to/empty-bundle
node examples/bounded-stream/run.mjs --replay /path/to/empty-bundle
node examples/bounded-stream/test-runtime.mjs /path/to/empty-bundle
```

When comparing emitters, reemit the **same saved graph** through the supported
emission operation, update its projection hash evidence, and run the unchanged
harness. Do not manually patch generated C#. The framework-only real-runtime
harness uses the installed Roslyn compiler/reference pack without NuGet restore.

The host supplies only the checkpoint and scheduling observation. Cursor creation,
advancement and link traversal are graph functions. Non-inlined setup and
inspection frames prevent the harness retaining head locals during collection.
Reflection counts the private pending registry only in this diagnostic harness.

## Scope

This repair removes the demonstrated TaskStart capture lifetime root. It adds no
Channel primitive, scheduling semantics or graph-authoring API. It does not prove
bounded memory for every graph or implement capacity-two admission, backpressure,
Stop/drain, cancellation recovery, exact-once sink delivery or throughput goals.
The [historical reproducer record](../../docs/verification/bounded-stream-cursor-retention-20261011.md)
remains the baseline evidence, not the current harness's expected result.
