# Large `.nyrec` allocation and real-commit probe

Investigation only. No production source is edited and no dependency is added.
`profile.py` builds the current Release projects into a temporary directory.
With `--instrument`, it copies sources and existing NuGet assets first, then adds
phase counters to that temporary copy. Each replacement requires exactly one
source anchor and fails if the code has drifted.

Run from a checkout whose .NET dependencies have already been restored:

```sh
python evaluation/nyrec-scaling/profile.py --output /tmp/baseline.jsonl
python evaluation/nyrec-scaling/profile.py --instrument --output /tmp/profiled.jsonl
python evaluation/nyrec-scaling/profile.py --instrument --deep --sizes 1000 --output /tmp/deep.jsonl
python evaluation/nyrec-scaling/profile.py --no-measure --sizes 1000,3000 --output /tmp/control.jsonl
python evaluation/nyrec-scaling/summarize.py /tmp/control.jsonl
```

For timing that matches the stock host's GC mode, prefix the probe command with
`DOTNET_gcServer=0`. The default FSI runtime uses Server GC in this environment;
the stock AuthoringSession runtime uses Workstation GC. Check the printed
`serverGC` value. The [optimization comparison](../../docs/verification/nyrec-optimization-20261025.md)
uses Workstation GC and frozen before/after sources with all other files held
constant. Earlier captured attribution runs used Server GC.

`--sizes` counts copies of the four-function verification planner, not bytes.
Function, parameter, node and binding IDs are remapped deterministically; type
declarations are shared. Both JSON and record round trips must produce identical
canonical JSON and record text. `--samples` defaults to three measured trials;
sample `-1` is a separate discarded warmup for each size/operation/format.

The session path is `evaluation/nyrec-scaling/.work/<random>/` **on the checkout's
filesystem**, not `/tmp`. Every trial restores and flushes the original file
outside the timer, opens a fresh writable session, simulates one function rename,
and calls the real `AuthoringSession.Commit`. Each API call has its own
`SnapshotComputationScope`, preserving ordinary retained validation/fingerprint
caches across requests. Postconditions check the committed name, snapshot ID,
parent ID, function count and successful reopen. Work files are removed in
`finally`; interrupting the entire host process may leave an ignored work folder.

The harness forces a full collection before each timed operation. This is a
controlled, warm-cache measurement, not a simulation of uninterrupted editing.
Use the uninstrumented `--no-measure` run for headline timings. Scope counters and
scratch probes are for attribution; they add overhead and can affect JIT/layout.
The current-thread byte counter measures cumulative allocations, not peak RAM.
The latest harness also captures process-wide allocated bytes, process CPU time,
GC pause duration and GC generation counts. Process CPU time sums all threads and
may exceed wall time, including concurrent GC and background JIT work.

`Scope` and `Phases` counters are **inclusive**. In particular:

- `semanticFingerprint` includes normalized-model validation and JSON writing.
- `record.json-reader-inclusive` includes the JSON model reader and validation.
- `json.validate-inclusive` includes all `validation.*` stages.
- `validation.functions` includes the `validation.fn-*` stages.
- `validation.fn-setup` includes resolver indexes **and eager type inference**.
- In the deep pass, `json.model` includes `json.ensure-properties`.

Do not sum parents and children. The summary computes independent medians; their
times need not sum to the median total. Earlier captured runs predate the extra
CPU/process/GC-pause fields; those fields are available in the control run.

Captured results and limitations: [2026-10-25 report](../../docs/verification/nyrec-scaling-20261025.md).
