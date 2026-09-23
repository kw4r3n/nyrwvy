# Semantic-facts scaling repair

**Status:** Observed result

Baseline: `23a2df0`. Comparison: the working-tree repair adding pass-scoped
snapshot indexes and per-function node-type memoization. This addresses the two
scaling regressions, not repeated validation across simulate/commit or pinned
dependency lifecycle behavior.

## Reproduction

```shell
dotnet build src/Nyrwvy.Execution/Nyrwvy.Execution.fsproj -c Release
dotnet fsi scripts/measure-semantic-facts.fsx
```

The [measurement script](../../scripts/measure-semantic-facts.fsx) was run before
and after the repair on the same Linux x64 machine with .NET SDK 10.0.112. Each
case has one warm-up and five samples; reported values are medians. Timings are
descriptive, not CI limits, and include default runtime tiered-JIT/GC behavior.
Fixtures are constructed outside measurements. Fingerprinting and typed-IR
projection include their validation costs; these rows must not be added together.

| Case | Before ms | After ms | Before allocated bytes | After allocated bytes |
| --- | ---: | ---: | ---: | ---: |
| Validate 100 functions, 14 nodes each | 27.900 | 13.437 | 16,024,224 | 7,928,912 |
| Validate 200 functions, 14 nodes each | 36.627 | 31.685 | 53,878,920 | 16,399,792 |
| Validate 400 functions, 14 nodes each | 97.621 | 24.153 | 203,668,696 | 33,707,208 |
| Validate 531 functions, 14 nodes each | 170.669 | 28.504 | 361,445,352 | 44,620,256 |
| Fingerprint 531 functions | 194.466 | 38.472 | 369,008,856 | 52,183,760 |
| Typed IR for 531 functions | 331.357 | 36.866 | 684,401,880 | 53,126,352 |
| Resolve result of 21-node shared ListConcat DAG | 1,041.707 | 0.019 | 2,299,728,200 | 32,808 |
| Validate 21-node shared ListConcat DAG | 3,097.153 | 0.059 | 6,828,809,400 | 88,848 |

Allocation counts are current-thread managed allocation totals, not retained
memory or peak RSS. The shared-list fixture is type-checked, never executed.

## Regression coverage

[SemanticFactsTests.fs](../../tests/Nyrwvy.Core.Tests/SemanticFactsTests.fs) adds
10 cases covering successful and failed shared-node resolution, node/query
ordering, cycle and missing-node handling, function-local memo isolation, all
four declaration indexes across revisions retaining a snapshot ID, and
validation/projection allocation scaling from 100 to 400 functions.

The shared-DAG test first checks a small fixture's allocation bound, so an
exponential regression fails promptly instead of hanging on the subsequent
65-node codec/fingerprint/projection checks. Allocation bounds avoid hardware
speed thresholds.

Verification run:

- Focused xUnit tests (`SemanticFactsTests`, `ValidationTests`,
  `TypedSemanticIrTests`): 29 passed.
- `fish scripts/test-fast.fish --no-restore --no-build`, after a Release test
  build: 348 F# tests and 28 Node tests passed.
- Existing NuGet packages were available locally; no restore was needed.
- No serialization/fingerprint format or semantic acceptance policy changed.

Snapshot indexes live for one pass, not in a global cache keyed by ID. Separate
validation/readiness/fingerprint calls still perform their own validation; this
record does not claim a reduction in validations per commit or an end-to-end
commit latency measurement.
