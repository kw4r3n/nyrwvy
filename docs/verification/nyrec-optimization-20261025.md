# Large `.nyrec`: optimization verification — 2026-10-25

The safe first changes reduce the 27.36 MiB rename commit from **2,921.1 to
1,313.3 ms (55.0%)**, and managed allocation from **3,349.6 to 1,728.5 MiB
(48.4%)**. This is a matched **Workstation GC**, counters-off, real
`AuthoringSession.Commit` API comparison. It is not MCP transport or process-startup
latency. Normalized-model validation, exact proposal fingerprints, ordered
request/receipt binding, source comparison and atomic persistence remain active.

This follows the [initial attribution and Opus audit](nyrec-scaling-20261025.md).
The earlier report's Server GC measurements are historical and are not the
baseline used here.

## Matched before/after results

Each value is the median of three samples after one discarded warmup. Allocation
is cumulative managed bytes on the calling thread, **not peak retained RAM**.
Each trial opens a freshly restored/flushed file, simulates a function display-name
rename, and commits through the ordinary session API. Reopening checks the
published name, snapshot/parent identities, function count and canonical roundtrip.

| Size / API | Before ms | After ms | Time reduction | Before MiB | After MiB | Allocation reduction |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 9.12 / nyrec open | 590.8 | 564.9 | 4.4% | 554.3 | 469.5 | 15.3% |
| 9.12 / nyrec simulate-rename | 769.8 | 656.6 | 14.7% | 1,028.2 | 817.0 | 20.5% |
| 9.12 / nyrec commit-rename | 775.0 | 401.4 | 48.2% | 1,076.7 | 558.0 | 48.2% |
| 27.36 / nyrec open | 2,107.5 | 1,783.7 | 15.4% | 1,699.5 | 1,429.1 | 15.9% |
| 27.36 / nyrec simulate-rename | 3,030.5 | 2,356.8 | 22.2% | 3,228.2 | 2,558.6 | 20.7% |
| 27.36 / nyrec commit-rename | 2,921.1 | 1,313.3 | 55.0% | 3,349.6 | 1,728.5 | 48.4% |

The 9.12 MiB rows use the reverse-order recheck described below. The 27.36 MiB
rows use the full first comparison. Both passes and every sample are retained.

### Validation and codec comparison at 27.36 MiB

| Size / API | Before ms | After ms | Time reduction | Before MiB | After MiB | Allocation reduction |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 27.36 / json commit-rename | 2,673.9 | 1,081.9 | 59.5% | 2,954.8 | 1,333.6 | 54.9% |
| 27.36 / json decode | 1,411.8 | 1,179.5 | 16.4% | 1,420.9 | 1,150.5 | 19.0% |
| 27.36 / json encode-cold | 995.1 | 847.9 | 14.8% | 1,102.8 | 902.0 | 18.2% |
| 27.36 / json open | 1,705.1 | 1,277.8 | 25.1% | 1,515.6 | 1,245.2 | 17.8% |
| 27.36 / json simulate-rename | 3,099.9 | 2,253.8 | 27.3% | 3,228.2 | 2,558.6 | 20.7% |
| 27.36 / model validate-cold | 896.5 | 694.2 | 22.6% | 892.7 | 715.6 | 19.8% |
| 27.36 / nyrec commit-rename | 2,921.1 | 1,313.3 | 55.0% | 3,349.6 | 1,728.5 | 48.4% |
| 27.36 / nyrec decode | 1,974.0 | 1,701.1 | 13.8% | 1,589.8 | 1,319.4 | 17.0% |
| 27.36 / nyrec encode-from-json | 233.0 | 232.1 | 0.4% | 376.1 | 376.1 | 0.0% |
| 27.36 / nyrec open | 2,107.5 | 1,783.7 | 15.4% | 1,699.5 | 1,429.1 | 15.9% |
| 27.36 / nyrec simulate-rename | 3,030.5 | 2,356.8 | 22.2% | 3,228.2 | 2,558.6 | 20.7% |

All changes were measured as one bundle. The `validate-cold` and codec rows show
the effects at their API boundaries; they do not establish a separate marginal
saving for each edit. `nyrec encode-from-json` is unchanged code, and its
allocation is unchanged. JSON remains part of both record parsing and commit
fingerprinting/serialization.

## Implemented changes

- **Validation indexes:** `Validation.fs` constructs the global node-owner lookup
  lazily as a pre-sized `Dictionary`. No obligations means no lookup construction.
  Invalid duplicate IDs still overwrite earlier entries, matching `Map.ofList`;
  local per-function resolver maps remain independent. Tests compare complete
  diagnostic lists for both duplicate-owner orders and missing targets/values.
- **Codec and hashing copies:** `SnapshotCodec.fs` decodes the written span of
  its exposable `MemoryStream` buffer without `ToArray`. `Fingerprint.fs` appends
  the original domain bytes, NUL separator and content bytes to SHA-256 instead
  of allocating a concatenated byte array. Domain identifiers and output bytes
  are frozen. The content UTF-8 allocation still exists.
- **JSON reader success path:** duplicate detection uses an ordinal `HashSet`;
  missing/unexpected sorted sets are built only on error. Every property name is
  decoded before reporting duplicates, preserving malformed-name precedence.
  Two common immutable property sets are shared. Required/allowed keys and
  duplicate > missing > unsupported diagnostic precedence are unchanged.
- **Normalized fingerprints:** removed the recursive identity reconstruction of
  `TypeRef` and its node payload traversal. All variants contain stable IDs or
  structure, with no display-name field; the previous mapper returned a
  structurally equal type reference by induction over those variants. Display,
  declared type-parameter and nested-binding normalization remains.
- **Rename review:** only requests consisting entirely of `RenameDisplayName`
  use the immutable base snapshot's semantic fingerprint. All nine rename target
  implementations change display text only, and normalization erases those
  fields plus revision identity. Thus normalized base/proposed models are equal
  for this specific operation class. The retained cache is reusable on commit's
  re-simulation. Accepted mixed operations still fingerprint the proposal.
  Unchanged visibility operations and empty requests retain their existing
  rejection before receipt creation.
- **Proposal effect order:** only equal, unique, ordered function IDs with
  reference-identical immutable bodies and equal required effects skip the
  whole-graph conflict comparison. These are exactly the inputs that determine
  observability and dependency order. Edited/new/reidentified functions, changed
  callee effects, reordered functions, equal cloned bodies and duplicate IDs take
  the existing path. Candidate validation still runs.

### Normalization validity investigation

The broad implication `validate s = [] => validate (normalize s) = []` is
plausible for the current model, but this change does **not** claim a general
proof or introduce an unchecked canonical writer. The normalizer preserves
semantic IDs, type/value/effect/capability structure and obligation relationships;
neutral names are nonblank, declared type-parameter names remain distinct, and
normalized obligation IDs are unique.

There is an additional public-contract constraint: `SemanticFingerprint.compute`
currently validates the **normalized** object, not the input. It accepts inputs
whose blank presentation names or self-parent metadata are repaired by
normalization. Replacing normalized validation with input validation would change
that behavior. Regression tests retain this acceptance and reject a semantic
result-type mismatch after normalization. This is why the implementation keeps
normalized validation and takes only the smaller identity-copy and rename-reuse
improvements. General validation elimination and cross-request inference caching
remain separate work requiring stronger invariant and dependency invalidation
arguments.

## Reproducibility and timing uncertainty

- Baseline revision: `a5b6f0b56242af081951cf9c2e8ec823130f8bc5`, with the
  investigation's working-tree sources frozen before editing. The after build is
  copied from that snapshot and overlays only the five changed production files.
  All other 87 mirrored source/config files are byte-identical. This holds other
  in-progress workspace edits constant; comparing against a fresh HEAD build
  would not do that. See [source hashes and harness hash](nyrec-optimization-20261025/comparison-provenance.json).
- Release assemblies, .NET SDK 10.0.113, Ryzen 9 5900X, Btrfs on NVMe. FSI is
  forced to Workstation GC with `DOTNET_gcServer=0`; logs report `serverGC=false`.
  Scope counters and source instrumentation are disabled. Actual work files are
  on the checkout filesystem, not tmpfs. See [environment](nyrec-optimization-20261025/environment.json).
- 1,000 planner copies = 4,000 functions / 35,000 nodes / 9,565,206 record bytes.
  3,000 copies = 12,000 functions / 105,000 nodes / 28,691,206 record bytes.
  Types are shared; functions are fixed-size, and there are no obligations or
  dependency sidecars in the timed fixture.
- A full GC precedes each measured operation; filesystem caches remain warm.
  Tiered compilation/PGO and background activity were not disabled. The first
  after pass at 9.12 MiB had slower cold validation/codec timings, including the
  unchanged record-from-JSON operation (78.1 → 129.8 ms). This prevents claiming
  a reliable small-input latency result from that pass alone.
- To check that concern, the same frozen builds ran again in reverse order at
  9.12 MiB: after, then before. Validation was 240.3 → 191.1 ms, JSON encode
  274.8 → 234.0 ms, JSON decode 411.5 → 371.6 ms and record decode 577.2 →
  541.6 ms. The unchanged record-from-JSON timing was 76.0 → 80.3 ms.
  The allocation reductions reproduce; timing remains specific to these runs.
  No pass was deleted. This is not an isolated-host or statistically powered
  latency study, and it does not prove a universal speedup for every input.
- Timing order was before/after for both sizes, then after/before for the small
  recheck. No tests or builds were launched during the full after pass or the
  reverse-order recheck; other host activity was not comprehensively logged.
  Process CPU time, process allocations, GC pause time and generation counts are
  also present in every raw row; scope/phase arrays are empty by design.

Raw data and machine-readable medians:

- [Before](nyrec-optimization-20261025/workstation-before.jsonl),
  [after](nyrec-optimization-20261025/workstation-after.jsonl),
  [comparison](nyrec-optimization-20261025/comparison.json).
- [Small recheck before](nyrec-optimization-20261025/workstation-before-recheck.jsonl),
  [after](nyrec-optimization-20261025/workstation-after-recheck.jsonl),
  [comparison](nyrec-optimization-20261025/comparison-recheck.json).

Replay with the [probe](../../evaluation/nyrec-scaling/README.md) from each source
state, with dependencies already restored:

```sh
DOTNET_gcServer=0 python evaluation/nyrec-scaling/profile.py --no-measure --sizes 1000,3000 --samples 3 --output /tmp/nyrec-control.jsonl
python evaluation/nyrec-scaling/summarize.py /tmp/nyrec-control.jsonl
```

## Correctness and review evidence

- Release build: **0 warnings / 0 errors** ([log](nyrec-optimization-20261025/build.log)).
- Core fast suite: **1,173 passed, 0 failed, 0 skipped**, with
  `dotnet test tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj --no-build --no-restore -c Release --settings tests/fast.runsettings`
  ([log](nyrec-optimization-20261025/core-tests.log)). The settings exclude
  `Suite=Extended`; that suite was not run.
- Node fast contracts: **39 passed, 0 failed**, via
  `node scripts/run-node-tests.mjs fast-contracts`
  ([log](nyrec-optimization-20261025/node-tests.log)).
- Both frozen builds independently decoded all three tracked `.nyrec` examples
  and all three record-format JSON fixtures. Current JSON content hashes, record
  content hashes, exact snapshot fingerprints and semantic fingerprints agree in
  all six cases ([before](nyrec-optimization-20261025/compatibility-before.jsonl),
  [after](nyrec-optimization-20261025/compatibility-after.jsonl),
  [comparison script](nyrec-optimization-20261025/compatibility.fsx)).
- Three pre-change fingerprint goldens (empty, milestone and Unicode) pass.
  Tests also pin frozen/current empty JSON, malformed and duplicate-key error
  precedence, nested escaped duplicate names, duplicate owner diagnostics,
  callee-effect fallback, metadata admission and ordered receipt binding.
- The real-commit probe's complete two-size runs and reverse-order small runs
  finished with all roundtrip, publication and reopen postconditions satisfied.
  Both regular/deep instrumentation anchor checks still match current sources.
- `git diff --check` passes. No public API, persistence extension policy or
  dependency was changed.

[Opus 5.5 review](nyrec-optimization-20261025/opus-review.md) found no blocking
correctness or trust-boundary issue in the fixed payload. [Provenance](nyrec-optimization-20261025/opus-review-provenance.json)
records its hash and disabled tool access. Its optional follow-ups were audited:

- Added nested unary-property duplicate tests, empty-request rejection,
  mixed no-op visibility coverage, and a test that fails when `RenameTarget`
  grows beyond the nine covered cases.
- Confirmed `new MemoryStream()` exposes its backing buffer and `StableId` uses
  structural equality/comparison over strings.
- Rejected the conditional NaN concern: `Float64Literal` holds `Float64Bits`, a
  `uint64` wrapper (`Foundation.fs:21,233`), so body equality does not inherit
  non-reflexive floating-point NaN equality. No compatibility relaxation is
  introduced by that hypothesis.
- Golden fingerprints were captured with the frozen **before** assembly,
  independently of the optimized output. The fixture output comparison below
  additionally checks both builds directly.

## Remaining limits and next targets

The bundle improves this metadata-edit workload. Generic structural edits still
compute the proposed normalized fingerprint; normalized validation is not removed.
A real workload containing many obligations is needed to measure the dictionary
lookup path's marginal benefit. One very large function, dependency sidecars,
transport, process startup and peak memory are not measured here; the Extended
suite was not run. No individual edit's latency contribution is isolated.

The remaining 27.36 MiB record commit still allocates about 1.69 GiB. Next useful
experiments are avoiding full normalized-model materialization/encoding with a
proved equivalent internal path, and reducing repeated local resolver work with
explicit dependency invalidation. The record-to-JSON compatibility bridge and
UTF-8 content arrays still allocate. Format changes alone do not remove those
costs. Exact source guards and atomic publication remain unchanged.
