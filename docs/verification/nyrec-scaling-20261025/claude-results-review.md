Model: claude-sonnet-5-5. Independent review of fixed aggregate measurements; no tools.

**Ranking of next optimization targets (by actual share, 3000-function record commit, total 3356 MiB / 2891 ms)**

Shares are computed from the separately measured medians. They are not additive in time.

1. **Full validator, 1786 MiB (53%), 1701 ms (59%).** This is the largest target by far. It runs 2 computations plus 4 cache hits.
   - Within the deep1000 breakdown (289.6 MiB), the largest sub-items are local fn-setup at 84.2 MiB (29%), per-function rules at 61.9 MiB, global setup indexes at 58.0 MiB (20%), and dependency-cycle at 20.8 MiB.
   - Resolver type inference at 46.0 MiB (16%) is the biggest single piece inside fn-setup. It is a **promising** candidate, but only if the inference can be restructured without changing issue order or messages.
   - The duplicated per-function nodesById/parametersById maps total at most about 20 MiB (resolver-indexes at 19.9 MiB plus the validator's own copies), roughly 7%. That is a small gain, and sharing them risks the per-function isolation you want to keep. I would rank it low.
   - The global nodeOwners `Map.ofList` over 35,000 nodes is a **promising** low-risk candidate. It is a subset of the 58 MiB and has not been measured separately.
   - Avoiding the second full validation is **not** available. The candidate and the fingerprint model are different objects, so this is not a redundancy.

2. **JSON writer, 650 MiB (19%), 464 ms (16%).** This covers 3 calls of bytes-to-string generation.
   - Writing directly to a UTF8 buffer or stream instead of building a string is **promising**. It would also remove part of the 27 MiB UTF8 array, but only for the persistence write.
   - The fingerprint encodings must stay byte-frozen, so any change needs byte-equality tests across all three writes. Sharing outputs between them is not possible because the schemas differ.

3. **JSON-to-record renderer, 376 MiB (11%), 233 ms.** This is a single call. It is a moderate target and likely overlaps with the writer, so improvements there may compound.

4. **Remaining other, about 238 MiB (7%).** This is unattributed and needs finer scoping before anyone acts on it.

5. **Effect-order check, 170 MiB (5%), 129 ms.** It is small and a plausible low-risk target.

6. **Source reread+compare, 110 MiB (3%), 37 ms.** This is small, and it is a safety check tied to your exact-request binding, so I would leave it alone.

7. **Atomic write+flush, 19.5 ms, about 0 MiB.** Do not touch it. It is negligible and carries the durability guarantee.

**Record decode (1898 ms, 1593 MiB)**
- This is a separate path from commit. It is 56% validator, which is the same top target again, so any validator win pays off twice.
- JSON parse/model building is 33% (530 MiB). `ensureProperties` is 60.9 MiB of about 176 MiB (about 35%) of the JSON model phase in deep1000.
  - Caching the property-name set or comparing without materializing the list is **promising**.
  - Malformed and duplicate rejection must be preserved.
  - The caller-built required/allowed literal sets are not covered by that number and may be additional waste.
- Record-specific work is only 11% and not worth pursuing.

**Promising versus proven**
Nothing here is a proven speedup. These are allocation shares, not time savings. Allocation correlates with time, but the validator is 59% of time versus 53% of allocation, which suggests compute cost beyond GC. Every target above is a hypothesis until it is implemented and measured in an A/B run.

**Remaining methodological flaws**
- **Profiler perturbation.** The instrumented scratch copy adds overhead. Its total (2890.8 ms) is below the original-assembly median (3034.1 ms), but the two were measured on different builds and runs, so per-category timings are not exact.
- **Inclusive versus disjoint scopes.** Fingerprint (1125 ms) overlaps with the validator and the JSON writer. Summing categories will mislead, as you noted.
- **Sample size.** Three measured samples per size make the medians fragile. They give no real variance estimate, and your own timing variance already rules out claiming JSON is faster.
- **Allocation is not time.** Cumulative allocation ignores GC generation effects, LOH behavior, and cache effects. LOH allocations from large strings and arrays in the writer may cost more than their MiB share suggests.
- **Deep pass at 1000 only.** The 3000 breakdown is extrapolated from the 1000 shares. Superlinear behavior, such as the Map or cycle detection, could shift the ratios.
- **Single synthetic shape.** The planner is replicated: four functions, 35 nodes, fixed types. The result may not represent real distributions of function size, so share estimates may not transfer.
- **Warm caches.** Warmed page cache and TLS buffers hide cold-start and I/O costs. The retain caches remain enabled, which hides the cost of cache misses.
- **Unattributed remainder.** About 238 MiB is uncategorized, and the ~176 MiB JSON model phase is only partly broken down.

**Recommendation:** start with validator type inference and indexing, then JSON writer direct-to-UTF8. Both need golden-output tests for issue order, messages, and fingerprint bytes before any claim of speedup.
