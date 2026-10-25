# Large `.nyrec`: allocations and real commit — 2026-10-25

This records the pre-optimization investigation. The [implementation follow-up](nyrec-optimization-20261025.md)
contains matched Workstation GC before/after measurements, compatibility checks
and the implementation review.

The largest measured commit costs are full semantic validation and JSON
generation; JSON model construction is also large on decode/open. Disk writing
is a small part of this workload. Optimization targets are identified below; no
production optimization or speedup is claimed.

## Real operations

These are original Release assemblies called under **FSI with Server GC**, with
scope counters disabled. Each value is the median of three measured samples
after one discarded warmup. Allocations are cumulative managed bytes on the
calling thread, **not peak RAM**. The stock AuthoringSession runtime configuration
uses Workstation GC when tested separately. These are real API-call measurements
under FSI, **not stock-host latency measurements**; matched-GC timing is pending.

| Record size | Functions / nodes | Open | Simulate one rename | Actual commit | Commit allocation |
| --- | --- | --- | --- | --- | --- |
| 9.12 MiB | 4,000 / 35,000 | 621 ms | 893 ms | 923 ms | 1,077 MiB |
| 27.36 MiB | 12,000 / 105,000 | 1,814 ms | 2,737 ms | 2,777 ms | 3,350 MiB |

At 27.36 MiB the measured commit samples were 2,720.5, 2,776.7 and 2,837.1 ms.
Median GC pause was 171.2 ms (about 6% of median elapsed time; individual sample
ratios were 4.3–7.2%). The generation counters reported 15 collections in total,
including 4 collecting generation 1 or higher and 2 collecting generation 2;
these are overlapping counts, not 21 separate collections. Process CPU time
was 5,406.6 ms, summed across all process
threads, including concurrent GC and background runtime work. GC pause alone
does not explain elapsed time; concurrent GC CPU cost is not isolated here.
Process-wide and calling-thread allocation counters differed by at most 0.008
MiB across the entire control run, supporting the synchronous attribution.

The original-assembly counters-on run measured 3,034 ms / 3,350 MiB for this
commit. The scratch-instrumented run measured 2,891 ms / 3,356 MiB. These separate
runs are subject to profiler/JIT/layout/system-load variation; do not interpret
their timing differences as an optimization or as a precise estimate of counter
overhead. Use the counters-off run for headline times and allocation shares from
the instrumented run for attribution.

## Allocation breakdown

### Actual commit, 27.36 MiB

Disjoint categories from the scratch-instrumented run:

| Category | Calls | Median allocated MiB | Share of 3,356 MiB |
| --- | --- | --- | --- |
| Full semantic validation | 2 | 1,786.0 | 53.2% |
| JSON writing, including output buffers/string | 3 | 649.9 | 19.4% |
| JSON-to-record rendering | 1 | 376.1 | 11.2% |
| Proposed runtime effect-order check | 1 | 169.6 | 5.1% |
| Exact-source reread/comparison | 1 | 109.7 | 3.3% |
| Final UTF-8 output array | 1 | 27.4 | 0.8% |
| Other, including normalization/hash/transaction work | — | approximately 237.6 | 7.1% |

Write plus `Flush(true)` measured about 19.5 ms and negligible managed allocation.
The measured persistence replacement wrapper, which also includes UTF-8 encoding
and rename, was about 31.5 ms. Independent timing medians are not additive.
The validation row includes about 6.4 MiB of probe overhead: the corresponding
uninstrumented baseline measured 1,779.6 MiB for validation and 3,349.9 MiB total.
This small overhead does not change the leading allocation categories.

The commit re-simulates the reviewed request and verifies its receipt
([Workspace.fs](../../src/Nyrwvy.Semantics/Workspace.fs#L284)). The two full
validations are of the proposed snapshot and a separately allocated normalized
model used by `SemanticFingerprint`, not two cache misses for the same object.
There were also four validation cache hits. The normalized model is built at
[Fingerprint.fs](../../src/Nyrwvy.Semantics/Fingerprint.fs#L180) and goes through
the canonical serializer and validator.

`semanticFingerprint` itself reported 1,208.6 MiB, **including** its validation and
JSON writing. It must not be added to the table. The three JSON writes include
two frozen fingerprint encodings and one current-schema persistence encoding;
these are different representations and cannot all share one serialized string.

### Simulate and the complete edit cycle, 27.36 MiB

The existing profiled data also attribute the rename simulation; no new run was
needed. Its total is 3,234.8 MiB (3,228.9 MiB in the counters-off control):

| Disjoint category | Median allocated MiB |
| --- | --- |
| Full semantic validation, two computations | 1,786.0 |
| JSON writing, three calls | 678.2 |
| Proposed runtime effect-order check | 169.6 |
| Other transaction/fingerprint/normalization work | 601.1 |

There is no record rendering or persistence here. Simulation is about 96% of
commit's allocation, but commit is **re-simulation plus persistence**, not the
original simulation plus a cheap write. An editing cycle pays both costs:
approximately 6,579 MiB across proposal and commit in the counters-off control.
The two requests validate and fingerprint equivalent proposed values again,
although they are newly constructed objects. Request/receipt re-simulation is a
correctness boundary; removing it is not proposed. Validation and fingerprint
improvements can benefit both requests, while record rendering affects commit.

### Record decode, 27.36 MiB

| Disjoint category | Median allocated MiB | Share |
| --- | --- | --- |
| Semantic validation | 894.7 | 56.2% |
| JSON parse/model construction, validation excluded | 529.6 | 33.2% |
| Record-specific header/rows/reference checks/materialization | 168.8 | 10.6% |

Total was 1,593.1 MiB. Record-specific processing still took roughly 0.4 s of the
1.9 s instrumented decode; a small allocation share does not make it free.

### Finer validation and reader attribution, 9.12 MiB

The deep pass measured full validation at 289.6 MiB:

| Section | Allocated MiB | Interpretation |
| --- | --- | --- |
| Per-function preparation | 84.2 | Includes indexes **and eager type inference** |
| Function/body validation rules | 61.9 | Rule checks and diagnostic-context preparation |
| Global indexes and initial type-reference checks | 58.0 | Includes the node-owner map for 35,000 nodes |
| Dependency-cycle checks | 20.8 | Separate from resolver type inference |
| Function contracts | 19.8 | Type-parameter/type-reference/effect checks |

Within the 84.2 MiB function preparation, resolver indexes account for 19.9 MiB
and eager type inference for 46.0 MiB. These are nested costs, not extra rows to
add. The validator builds local node/parameter maps at
[Validation.fs](../../src/Nyrwvy.Semantics/Validation.fs#L489), while the resolver
builds its own maps at
[SemanticFacts.fs](../../src/Nyrwvy.Semantics/SemanticFacts.fs#L78).
The resolver precomputes completion/type facts before returning; its whole setup
cost must not be described as indexing alone.

The deep JSON reader executed `ensureProperties` 87,024 times and allocated
60.9 MiB inside it, versus about 176.6 MiB for the whole JSON model phase before
deep-probe overhead. Property-name strings/lists, `Set.ofList` and two set
differences are materialized for every object
([SnapshotCodec.fs](../../src/Nyrwvy.Semantics/SnapshotCodec.fs#L58)). Caller-built
required/allowed sets are outside the 60.9 MiB submetric.

## Improvements to evaluate

Largest category and smallest worthwhile first change are different rankings.
After Opus review, the proposed experiment order is:

1. **Start with byte-identical buffer/copy changes.** JSON
   writing plus record rendering account for about 30.6% of commit allocation,
   but that is the combined opportunity, not the saving from one small change.
   `writeSnapshotAt` grows a `MemoryStream`, calls `ToArray`, then decodes a
   string ([SnapshotCodec.fs](../../src/Nyrwvy.Semantics/SnapshotCodec.fs#L1332));
   `RecordCodec.ofJson` parses that string back to JSON and builds record-line
   strings and a final `StringBuilder` result
   ([RecordCodec.fs](../../src/Nyrwvy.Semantics/RecordCodec.fs#L1028)). Avoiding
   `ToArray` is a small first experiment. Also inspect the fingerprint hash path:
   it creates a UTF-8 content array and another domain-plus-content array
   ([Fingerprint.fs](../../src/Nyrwvy.Semantics/Fingerprint.fs#L15)). Incremental
   hashing of the same bytes is a candidate; its individual allocation saving
   has not been instrumented. An internal UTF-8 path is a larger follow-up.
   Preserve canonical JSON, record bytes and all frozen fingerprints. The fixture
   JSON sizes are 8,253,086 bytes at 1,000 copies and 24,755,086 at 3,000; the
   writer's measured cost also includes traversal/sorting/token work, so it must
   not all be labelled buffer copying.
2. **Evaluate the normalized-fingerprint validity invariant early.** The
   normalized model is a new object, so ordinary identity-cache reuse cannot
   remove its full validation. That alone is not a reason to defer studying
   whether `validate s = []` implies `validate (normalize s) = []` for every
   supported construct. Establish that invariant through normalizer/validator
   analysis and differential/property tests before considering a trusted
   internal encoding path. Corpus tests alone are not a general proof. Do not
   remove validation on an unproved assumption. This potentially benefits both
   simulate and commit; exact savings for that individual validation were not
   separately measured.
3. **Within validation, investigate global indexes before broad inference
   refactoring.** Inspect `nodeOwners`
   ([Validation.fs](../../src/Nyrwvy.Semantics/Validation.fs#L229)), repeated
   collections/maps and resolver data structures
   ([SemanticFacts.fs](../../src/Nyrwvy.Semantics/SemanticFacts.fs#L606)). In the
   same profiled build, tripling the function count increased setup median time
   by 4.05x for simulate, 3.71x for commit and 4.68x for decode; function-validation
   times grew by 3.07x, 2.60x and 2.90x. This supports prioritizing setup, not an
   asymptotic complexity claim. The 58.0 MiB deep stage is broader than
   `nodeOwners`; no individual replacement's saving is established. A dictionary
   experiment must preserve last-wins behavior on duplicate IDs, all diagnostics
   and each function's node-ID ownership isolation. Cross-request validation
   caching needs dependency invalidation and is not the first proposed change.
4. **Reduce property-check allocations on the JSON reader's successful path.**
   First assess hoisting truly constant required/allowed sets: the readers build
   them on every call, for example
   [SnapshotCodec.fs](../../src/Nyrwvy.Semantics/SnapshotCodec.fs#L1442).
   The directly measured 60.9 MiB subcost is about one third of the 9.12 MiB JSON
   model-construction allocation. Preserve duplicate-key detection, malformed
   input rejection, allowed/required fields and diagnostic behavior. This helps
   record decode as well because it delegates to the JSON model reader. These
   reader changes do not affect the measured simulate/commit path.

The effect-order check (5.1%) is a secondary candidate: even a display-name rename
precomputes observability and checks all proposed functions
([GraphOrder.fs](../../src/Nyrwvy.Semantics/GraphOrder.fs#L189)). A shortcut must
prove that changed callee observability cannot introduce a conflict in an
otherwise unchanged caller. Source comparison (3.3%) is a lower priority; byte
comparison would change the current BOM/UTF-8 decoded-string comparison semantics.
Do not weaken request/receipt binding, source comparison or atomic flush/rename.
Changing back to JSON removed about 395 MiB of commit allocation at this size,
but left approximately 2,955 MiB. Format choice alone does not address most of
the cost; the timing samples do not establish a general JSON speed advantage.

## Claude review and verification

Claude Sonnet 5.5 reviewed explicitly authorized, fixed source excerpts and then
the aggregate results. Tool access, MCP and repository exploration were disabled
for both successful calls. The [method review](nyrec-scaling-20261025/claude-method-review.md)
led to the counters-off control and the process-allocation/CPU/GC-pause
cross-checks. Its missing-context questions about nested scopes and I/O counters
were resolved by checking `Start` and the actual persistence measurement points.
The [results review](nyrec-scaling-20261025/claude-results-review.md) agrees on
validation and JSON writing as the leading targets.

The reviews are leads, not authoritative measurements: the final review's
"3000-function" heading means 3,000 copies / 12,000 functions; the main
27.36 MiB categories were measured directly, while only the finer resolver and
property-check subcategories were measured at 9.12 MiB. Its claim that different
snapshot objects make all future validation elimination impossible is too broad:
they prevent simple identity-cache reuse, not a separately proven alternative
encoding design. No numeric saving for duplicate map elimination is claimed.

### Opus audit

[Opus 5.5's independent review](nyrec-scaling-20261025/opus-review.md) received
the report, harness, related source excerpts and all three measured raw samples
for the reviewed groups. [Provenance](nyrec-scaling-20261025/opus-review-provenance.json)
records the fixed payload hash. It reproduced the headline values, allocation
arithmetic and non-overlapping commit/decode categories, and conditionally
accepted the investigation with corrections.

Accepted and checked findings:

- Added simulate attribution and distinguished total opportunity from the
  benefit of one minimal change; revised the experiment order above.
- Confirmed setup scaling using the same profiled build, not the differently
  instrumented deep build. It is a signal, not proof of `O(n log n)` behavior.
- Confirmed the extra UTF-8/hash arrays, reader-created constant sets and
  whole-snapshot proposal-order work from previously omitted source. Their
  individual optimization savings remain unmeasured.
- Corrected GC collection-count wording and annotated probe overhead.
- **Confirmed the GC-host mismatch.** The stock runtimeconfig has no Server GC
  setting. Running FSI with that runtimeconfig reported `IsServerGC=false`,
  whereas the benchmark FSI configuration reported `true`. A separate
  `DOTNET_gcServer=0 dotnet fsi` check also reported `false`. A matched-GC large
  control run has not been performed; the latency/GC numbers must not be
  generalized to the stock host yet. Re-run with, for example:

  ```sh
  DOTNET_gcServer=0 python evaluation/nyrec-scaling/profile.py --no-measure --sizes 1000,3000 --output /tmp/nyrec-workstation-control.jsonl
  ```

Not adopted as established results: a specific speedup from buffer/index changes,
an exact byte allocation for normalized validation or hashing alone, the claim
that finite property tests prove normalization valid for all models, and a
complexity conclusion from two sizes. No optimization was implemented for this
audit.

Captured runs completed round-trip and real-commit postcondition checks. The raw
data were checked for complete groups of warmup plus three samples: 132 baseline,
132 instrumented, 44 deep and 88 control records. Every instrumented commit had
two validation computations and three JSON writes. Release builds passed. No
production source was modified; full functional suites were not rerun.

## Reproduction and limits

- [Harness and commands](../../evaluation/nyrec-scaling/README.md)
- [Environment](nyrec-scaling-20261025/environment.json)
- Raw [baseline](nyrec-scaling-20261025/baseline.jsonl),
  [instrumented](nyrec-scaling-20261025/profiled.jsonl),
  [deep](nyrec-scaling-20261025/deep.jsonl), and
  [counters-off control](nyrec-scaling-20261025/control.jsonl)

Revision `a5b6f0b56242af081951cf9c2e8ec823130f8bc5`, .NET 10.0.113 Release
assemblies, F# Interactive harness, Ryzen 9 5900X, Server GC. Session files were
on NVMe/Btrfs; temporary instrumented build sources were in tmpfs. Every sample
forced full GC before its measured operation. Page cache, JIT and reusable
decode buffers were warm. Three samples support a local probe, not a production
latency guarantee or a claim about asymptotic complexity. No peak RAM, LOH
allocation distribution, cold process startup, wire/MCP transport, dependencies,
long editing history, multi-function edits, or giant single-function graphs were
measured. Optimization savings remain unproven until implemented and checked.
