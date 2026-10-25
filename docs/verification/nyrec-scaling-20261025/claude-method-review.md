Model: claude-sonnet-5-5. Review of a fixed, explicitly authorized source excerpt; no tools. Some missing-context concerns are resolved in the main report.

# Review: AuthoringSession open → rename proposal → Commit profiling

I reviewed only the payload. Several things I'd need to see are not in it: `AgentWorkflow.commitWithPolicy`, `SessionStorage`, `serializeCanonical`, `AllocationProbe`, `SnapshotComputationScope.Start` (lines 49–84), the end of `Memoize` (line 139 onward), and `SnapshotValidator.validate`. Anything depending on them is labelled a hypothesis.

## 1. Measured evidence and hypotheses

**Measured (as you reported, n=3 after 1 warmup):**
- 9.12 MiB record: load ~0.6s; proposal ~0.86s / 1028 MiB; commit ~0.89s / 1077 MiB.
- 27.36 MiB record: commit ~3.03s / 3350 MiB.
- Small-input counters: commit performs 2 full validations and 3 JSON writes.

**Derived arithmetic, not new measurements:**
- Input grew 3.0×; commit time grew 3.4× (97 → 111 ms/MiB) and allocation 3.11× (118 → 122 MiB per MiB of input).
- This is consistent with roughly linear work. The 14% time excess is not distinguishable from GC or cache noise with n=3, and two sizes cannot establish asymptotics.

**Not measured, and nothing in the payload shows it:**
- Which code produced the 1 GiB.
- Whether GC, I/O or managed CPU dominates.
- Whether the 2 validations and 3 JSON writes occur on the same snapshot object.
- Whether the counts and their cost shares hold at 27 MiB.
- fsync/rename cost on Btrfs.

## 2. Methodology corrections

1. **Instrumentation is always on.** `profile.fsx:48` passes `measure = true` unconditionally, so the headline times include the `Memoize` bookkeeping and the probe.
   - Add a `measure = false` control for headline time.
   - Use `measure = true` only for attribution.
2. **Possible nested-scope blind spot.** `measure` opens a scope at `profile.fsx:48`, and `SimulateOperations` (`Session.fs:1262`) and `Commit` (`Session.fs:1453`) call `Start()` again.
   - I can't see whether the inner `Start` joins the outer context or creates a new one.
   - If it creates a new one, `scope.Measurements` (`profile.fsx:60`) will miss session-internal work.
   - Confirm where the "2 validations / 3 JSON writes" counters came from: Scope or Probe.
3. **Silent probe failure.** `profile.fsx:13–17` returns `[]` if the reflected `AllocationProbe` type is absent. Assert that it exists, otherwise "Phases" can silently be empty.
4. **Counters are inclusive and name-keyed only** (`SnapshotComputationScope.fs:8, 26–36`).
   - There is no parent link, so self time can't be derived.
   - A same-name recursive nesting double-counts.
   - Only `Memoize`d work is attributed; everything else is invisible.
   - Record a parent name or depth to get exclusive cost.
5. **Allocation accounting is single-thread.** `profile.fsx:51, 55` and `SnapshotComputationScope.fs:42` use `GetAllocatedBytesForCurrentThread`.
   - Cross-check with `GC.GetTotalAllocatedBytes()`, which is process-wide.
   - Divergence means other threads are allocating.
6. **Allocation volume is not time.** ~1 GiB in ~0.9s is a few GB/s of allocation; that alone doesn't imply GC-bound.
   - `profile.fsx:59` records Gen0/1/2 counts, but they weren't reported.
   - Add `GC.GetTotalPauseDuration()`, LOH allocation, peak working set, and process CPU time versus wall time.
7. **Sample count.** 3 samples is too few for a 3s operation.
   - Report min, median and all samples.
   - Use ≥3 sizes (the `sizes` argument supports it) to check scaling.
   - Drop sample −1 in analysis explicitly.
8. **Missing environment.** Record Debug versus Release for the Nyrwvy assemblies, `dotnet fsi` flags, Server versus Workstation GC, `gcConcurrent`, TieredCompilation/PGO, the .NET version, and the Btrfs mount options (especially `compress=`).
9. **Warm-state caveats.**
   - The page cache is warm (`profile.fsx:94–97` fsyncs and then immediately opens).
   - Btrfs background work from the previous sample's truncate or rename can leak into the next one.
   - `RecordCodec.fs:392` makes decode allocation history-dependent: the thread-local `ArrayBufferWriter` only grows, retaining LOH memory.
   - Ask whether production is a long-lived process or one process per command. If it's per command, the warmup-excluded numbers understate JIT cost, and the retained cache (`SnapshotComputationScope.fs:90–95`) never helps.
10. **Fixture representativeness.**
    - The synthetic fixture replicates one function graph (`profile.fsx:24–38`), and a rename touches one function. That is the best case for any structural-sharing optimization.
    - Add a large multi-function edit workload before generalizing.
    - Report per-node and per-function normalizations using the `profile.fsx:81` output (`jsonBytes` and `recordBytes` weren't given; the JSON/record size ratio is needed to size item 3 below).
11. **I/O isolation is missing.** No timer exists around `replaceAtomically` (`Session.fs:1129`) or the source read (`Session.fs:1108`). Add them, or run `strace -T -e trace=fsync,fdatasync,rename,read`.

## 3. Ranked optimization candidates

All of these are hypotheses until their evidence arrives. Rank is by expected payoff weighted by safety.

**1. Redundant work across simulate → commit → persist (highest value, gated on evidence).**
- *Locations:* `Session.fs:1492–1495`, `Session.fs:1119–1123`, `Fingerprint.fs:180–193`, `RecordCodec.fs:1052–1053` (the comment says `serialize` validates), `SnapshotCodec.fs:2663`.
- *Why suspect:*
  - Retained caches are keyed by snapshot reference (`SnapshotComputationScope.fs:98, 132`), and each request has its own scope. A candidate rebuilt by `commitWithPolicy` would be a new object and a cache miss.
  - `Fingerprint.fs:181` builds a fresh `normalized` snapshot. If `serializeCanonical` validates, that fresh object forces another full validation that can never hit the cache.
  - This could explain the 2 validations and 3 JSON writes, but it is a guess.
- *Evidence needed:* per-call trace of (computation name, callsite, `RuntimeHelpers.GetHashCode(snapshot)`, ticks, bytes), taken at 27 MiB. Decide whether the two validations are the same object, content-identical but distinct objects, or different inputs.
- *Preserving invariants:* only reuse results by identity or a validated witness; never skip validation of an object that has not been validated.

**2. Byte-level serialization pipeline (low risk, output byte-identical).**
- *Locations:*
  - `SnapshotCodec.fs:1333–1338`: `MemoryStream` with default capacity (doubling growth, LOH), then `ToArray`, then `GetString`.
  - `RecordCodec.fs:1029`: `JsonDocument.Parse(string)` transcodes back to UTF-8.
  - `RecordCodec.fs:1031–1050`: per-line strings → `StringBuilder` → `ToString`.
- *Hypothesis:*
  - Each JSON write allocates several multiples of its output size.
  - The bytes → string → bytes round trip on the record path is avoidable.
  - Use an `ArrayBufferWriter` and an internal bytes API.
- *Evidence needed:* per-phase bytes against `jsonBytes`. The share depends on the JSON/record size ratio.
- *Verification:* golden byte-equality of JSON and record output, and unchanged fingerprints.

**3. Fingerprint structural sharing (medium risk).**
- *Location:* `Fingerprint.fs:184–190`.
- *Idea:* cache normalized or canonical segments per entity by reference, so unchanged functions are not renormalized, as long as the concatenated byte stream equals the old one.
- *Evidence needed:* the fraction of function objects reference-shared between base and candidate (unknown, depends on `AgentWorkflow`), plus the self cost of `semanticFingerprint`.
- *Verification:* golden tests against known fingerprints.

**4. Validation internals (high potential, highest risk).**
- *Locations:* `Validation.fs:489–499` (per-function `Map.ofList` / `Set.ofList` built eagerly), `Validation.fs:505–507` (per-node generic equality on `completion dependency = Some …`), `Validation.fs:2766–2770` (eager maps in `validateProposal`), `Validation.fs:2784`.
- *Do not chase:* the reflection at `Validation.fs:2481` and `2775` and the `collectNodes` search at `2772` sit on error-message paths. On valid input they are likely cold, but that is from reading the code, not measuring.
- *Caveat:* per-function reuse is unsafe unless the dependency closure (callees, types, effects) is tracked.
- *Evidence needed:* self time and allocation by validation section from a CPU or alloc sampler (`dotnet-trace`, `perf` with `DOTNET_PerfMapEnabled=1`). Attempt this only if validation is still dominant after item 1.
- *Verification:* differential tests on issue lists, including order and message text.

**5. Exact-source check (small payoff, low risk).**
- *Location:* `Session.fs:1108–1110` reads the whole file into a string and compares it.
- *Idea:* a chunked byte compare with early exit avoids a 2n-byte UTF-16 string (estimated ≤ ~3× file size in allocations).
- *Constraint:* keep it exact, and keep the check after serialization and immediately before the replace (`Session.fs:1126–1129`). Don't hoist it earlier.
- *Unknown:* what `SessionStorage.encode` (`Session.fs:1123`) does (BOM or newline handling), which affects how to compare.

**6. Open-path decode (affects the ~0.6s load, not proposal or commit unless they decode).**
- *Location:* `SnapshotCodec.fs:61–83` runs a `Seq.map` + `List.ofSeq` + `Set.ofList` + two `Set.difference` per JSON object, and allocates every property name string.
- *Idea:* a no-allocation happy path that builds error text only on failure, keeping the exact messages and their order.
- *Other:* `RecordCodec.fs:394–401` (`editDistanceOne`) is likely diagnostics-only; leave it unless a trace shows it. Any replacement for the sort at `RecordCodec.fs:1044` must be tie-compatible, because `List.Sort` is unstable.

**7. Diagnostic experiments (no code changes).**
- Run with different GC settings, e.g. `DOTNET_gcConcurrent=0`, `DOTNET_GCgen0size` increased (if honored by your runtime), and Server GC.
- A large time drop would indicate GC-bound behavior.
- Combine with the pause-time metric from section 2.

## 4. Minimal next measurement set

1. 27 MiB commit with the per-call trace from item 1, plus stopwatch/alloc around:
   - `commitWithPolicy` (`Session.fs:1492`),
   - serialize (`Session.fs:1119`),
   - `checkPersistedSource` (`Session.fs:1126`),
   - `replaceAtomically` (`Session.fs:1129`).
2. GC pause time, Gen counts, and LOH bytes per phase.
3. One sampling-profiler run.
4. `jsonBytes` and `recordBytes` (`profile.fsx:81`), and the Scope/Probe split of the "2/3" counters.
5. An A/B of `measure` on versus off.

Do not act on candidates 4–6 until the trace shows the target at more than roughly 10% of its phase's self cost.
