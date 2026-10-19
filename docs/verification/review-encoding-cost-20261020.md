# Review encoding CPU and allocation measurements — 2026-10-20

Baseline: `fca69b346fe03eefe6dc35335462e67d833ba9d5`. Local generated graphs isolate response encoding from authoring, validation and fingerprint computation. No remote CI, live provider, migration rerun or reader retirement was involved.

## Measured gap

`detail: full` without `structuredReview` formats each flattened node with F# `%A`. A parent node includes its nested regions, repeating those regions in descendant entries and creating substantial temporary formatting allocation. The saved HTML engine-review section used that same representation even though it already contained structured visual before/after focus.

`readProposal` had an 8 KiB response bound, but first serialized and parsed the entire encoded full review and then serialized/parsed selected values again. A small page could therefore incur allocations proportional to a large unselected literal. Compact projection used the same serialize/parse round trip.

The existing 256-node structural recipe limit rejected the initial wider single-request fixtures. Final large fixtures begin with a valid small proposal, enlarge only its in-memory graph, and pass `SnapshotValidator`; production authoring limits are unchanged. Cases include a flat 2,049-node graph, a 201-node graph with 24 nested regions and 128 leaf items, and a one-node 1 MiB String graph. No snapshots/examples were modified.

## Candidate

- Saved engine-review JSON uses the complete structured full encoder. Every review field and nested node remains in the durable artifact; it does not depend on a pending proposal surviving. The HTML/model wording identifies this structured representation. Explicit protocol full/debug and structured full formats remain unchanged.
- Compact and page projections traverse existing wire dictionaries/arrays and select the requested subtree directly. They retain the existing budgets, omission metadata, original indices, UTF-16 paging boundaries and exact identity rules.
- Exact original/output byte counts use the same JSON serializer/options against a counting stream rather than retaining additional serialized strings. No second full JSON DOM is built solely to project a page. The source encoding object is not mutated.

No new configuration, global graph cap or silent truncation of complete saved reviews was added.

## Results

Medians of three fresh processes per row, same machine/runtime and validated fixture shape. Allocations are cumulative managed bytes during encoding, **not simultaneously resident memory**. CPU uses process CPU-time deltas; small timings include encoder/JIT setup. Peak RSS includes runtime, graph construction, validation and warmup, so it is a separate whole-process observation. Internal rows measure the encoding value and compact JSON serialization, excluding final HTML rendering and indentation; those layers are not claimed to have a process-wide bound.

| Case | CPU ms before → after | Allocated bytes before → after | Response bytes before → after | Peak RSS bytes before → after |
| --- | --- | --- | --- | --- |
| Nested internal engine review | 3520.867 → 8.845 | 1,845,614,040 → 323,688 | 2,407,689 → 30,300 | 128,806,912 → 101,429,248 |
| 1 MiB literal, one-node page | 35.850 → 27.901 | 27,312,960 → 6,316,040 | 754 → 754 | 121,049,088 → 113,745,920 |
| Same literal, receipt-only page | 25.543 → 25.892 | 9,463,600 → 13,408 | 449 → 449 | 113,991,680 → 113,041,408 |
| Flat 2,049-node graph, one-node page | 25.394 → 20.388 | 4,856,976 → 1,807,472 | 735 → 735 | 103,952,384 → 102,785,024 |

Receipt-only CPU did not improve; its improvement is allocation. The smaller structured internal response represents the same complete review, with typed node data instead of repeated debug strings. Paging budgets remain 16,384 bytes for compact review and 8,192 bytes for a page.

## Verification and reproduction

Focused Release selection: **44/44** across `CompactSimulateReplyTests`, `ViewerArtifactTests`, `ViewerTests`, `JsonDepthBoundTests` and fast `PureRunLoneSurrogateTests`. New checks cover exact byte counts for escaped controls, astral text and lone surrogates; source nonmutation; escaped JSON pointers and noncanonical array indices; allocation independent of an unselected 1 MiB payload; and a saved complete structured engine review containing all 37 nested test nodes. Existing tests cover stale/rejected proposals, commit parity, omission paths and surrogate-split rejection.

Review identified that a compact saved engine review would omit durable details; that approach was corrected to complete structured data before the final tests. No unrelated broad suite was rerun. No source branch, PR, CI dispatch or deployment was published.

Local task-3 evidence: `encoding-bench/` and `encoding-bench-baseline/` contain the standalone friend-assembly harness, validator-checked graph generator, raw per-case JSONL and repeated samples. `encoding-bench/summary.json` and `repeats.json` support the table. Build/run each `Bench.fsproj` in Release and use its `run.py`; `repeat.py` compares the four repeated cases. The baseline worktree is pinned to the SHA above. `encoding-focused-final.log` and `encoding-focused-final.trx` record the focused run. Initial recipe-limit failures remain in the exploratory logs and are not treated as performance samples.

## Remaining limits

These changes remove the measured repeated-debug cost from internal saved reviews and avoid full-review JSON copies in projections. They do not impose a hard CPU/RSS cap. The initial structured encoding still traverses the graph, exact selected-value byte counts still scan selected payloads, and serializer buffers can scale with a large selected String. An explicit legacy unstructured full response still uses `%A` and retains its measured cost; callers can already request `structuredReview: true` or bounded `readProposal` pages. A new refusal threshold or external resource limit would change compatibility and is not justified as an arbitrary default by these measurements alone.
