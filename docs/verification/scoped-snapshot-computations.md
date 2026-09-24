# Request-scoped snapshot computation reuse (#210)

**Status:** Observed result

Baseline: `41efb2f` (the index/memoization fixes and retained dependency pins are
already present). SDK 10.0.112, Release, Linux x64.

## Implementation boundary

`SnapshotComputationScope` retains derived validation, canonical serialization,
structural fingerprint and semantic fingerprint results only for one synchronous
request/pass. Keys use snapshot **object identity**, not SnapshotId or structural
equality. A different immutable snapshot must take the full correctness path.
Failures are cached as results too; thrown exceptions are not memoized.

AuthoringSession public mutation/status/execution and open/create/pin boundaries
open scopes. The JSONL boundary also opens a scope so nested status/readiness work
shares the same request. Nested scopes reuse the parent; final disposal clears
all retained snapshots. Concurrent readers of one inherited scope are serialized
around the typed caches. No-scope callers retain the uncached path.

No validation rules, receipt/request comparisons, re-simulation, stale-base,
external-file/dependency checks or commit policies were removed. File reads,
publication checks and writes are **never cached**. This is not incremental
validation and does not treat a matching snapshot ID as proof of validity.

## Reproduction and measurements

```shell
dotnet build tools/Nyrwvy.AuthoringSession -c Release
dotnet fsi scripts/measure-authoring-passes.fsx
```

The [probe](../../scripts/measure-authoring-passes.fsx) performs real Session
simulate+commit with 14 nodes per existing function. Setup/open is outside the
measurement; simulate and commit have separate scopes, as in ordinary requests.
`reuse=false` reproduces uncached work in the same build; `reuse=true` uses normal
request reuse. The selected sample is the median total latency of three runs
after warm-up. Per-computation costs are inclusive/overlapping and must not be
summed; phase totals include all work. Timings are descriptive, not CI thresholds.

| Functions | Operation | Validation runs before → after | Total simulate+commit ms before → after |
| --- | --- | --- | --- |
| 400 | Rename | 14 → 6 | 340.758 → 146.444 |
| 400 | Create function | 11 → 6 | 247.383 → 137.807 |
| 531 | Rename | 14 → 6 | 448.491 → 205.869 |
| 531 | Create function | 11 → 6 | 319.288 → 195.016 |

For 531-function rename, simulation validation took 202.978 → 73.109 ms and
commit validation 185.708 → 91.404 ms. Commit canonical serialization took
121.974 → 34.697 ms (inclusive); actual same-directory file replacement was
0.615 → 0.677 ms and ran exactly once in both modes. Persistence was not the
latency driver in this local workload. Fingerprint rows are also emitted
separately by the probe. No filesystem durability upgrade is claimed.

## Regression evidence

Six new cases cover valid/invalid result reuse, equal-ID/different-content
isolation, exact codec/fingerprint behavior, nested/exception disposal, concurrent
readers, and rename/create validation-count bounds through actual persistence.
Each workflow must use at most six full validations rather than the uncached
10+ and still perform one real replacement with the acknowledged persisted ID.
There is no wall-clock test gate.

Focused scope/receipt/session/pin tests: 40 passed. Fast suite: 383 passed.
The existing exact-intent, stale proposal, persistence failure and changed
manifest tests continue through the maintained scoped authoring boundary.
