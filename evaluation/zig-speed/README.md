# Zig path speed

Wall time of the Zig tools against the F# path, warm, as the median of 9 runs after one warm-up, in ms. Runs are local and Release, on one machine, so only the ratios within a run are meaningful. The test is `ZigSpeed` in `tests/Nyrwvy.Core.Tests/ZigSpeedTests.fs` (`Suite=Benchmark`, which neither suite runs):

```bash
NYRWVY_SPEED_RHIFILL=<copy of a resolved workspace.json> dotnet test tests/Nyrwvy.Core.Tests -c Release --settings tests/benchmark.runsettings --logger "console;verbosity=detailed"
```

Rows:
- **(1) open:** `AuthoringSession.openReadOnly`, once Zig-decided and once with `NYRWVY_SNAPSHOT_READER=fsharp`.
- **(2) to Zig source:** runPure's whole program up to the runner source.
  - The Zig route: `ModelJson`, then `model-reader --execution-ir`, then the Zig emitter.
  - `fsharp+zig-emitter`: the F# lowering with the Zig emitter, the route before #534.
  - `fsharp`: the F# lowering and the F# emitter.
- **(3) tools:** the steps on their own.

Inputs:
- `task-outcomes.nyrec` (60 KiB). It is also the largest validator-corpus model.
- Rhifill's merged model (453 KiB). A resolved workspace reads with F# only, so it is timed as a plain JSON snapshot.

## Validating each model once (2026-10-28)

| | before | after | F# |
|---|---|---|---|
| task-outcomes: open | 16.7 | 13.4 | 11.3–11.5 |
| task-outcomes: to Zig source | 14.8 | 11.6 | 10.1 (12.3 with the Zig emitter) |
| Rhifill: open | 113.2 | 70.5 | 38.4–46.1 |
| Rhifill: to Zig source | 92.3 | 63.7 | 71.0–74.7 (77.1–80.3 with the Zig emitter) |

Breakdown for Rhifill, after:
- (2): `ModelJson` 0.8 (was 7.1, the writer), `model-reader --execution-ir --validated` 41.4 (was 63.2), emitter 21.5.
- (1): `model-reader --validate` 41.0, F# Snapshot build 6.7, F# validate 19.4.
- Process spawn: 0.8.

The open on the Zig path is still slower than F#, because the Zig validator (41) is slower than the F# one (16–19). The validator hotspot work is the next change. The F# column varies by up to about 20% between runs on the larger input.

## Zig validator hotspots (2026-10-28)

The changes, with outputs unchanged:
- the requirement table indexed per node kind;
- messages built only for a mismatch;
- kind lists split at compile time;
- an ASCII fast path in the JSON parser;
- dependencies and observability memoised per node in the effect-order search (reused by the lowering);
- no canonical text written when only the shape is read.

| | before (#535) | after | F# |
|---|---|---|---|
| task-outcomes: open | 13.4 | 12.4 | 11.3–13.1 |
| task-outcomes: to Zig source | 11.6 | 10.1 | 10.1–12.5 |
| Rhifill: open | 70.5 | 53.3 | 38.4–71.9 |
| Rhifill: to Zig source | 63.7 | 50.1 | 71.0–74.7 |
| Rhifill: `model-reader --validate` | 41.0 | 23.5 | 16–20 (`validate` alone, without the 7 ms Snapshot build) |
| Rhifill: `model-reader --execution-ir --validated` | 41.4 | 27.5 | — |

For Rhifill's validation, before (in ms):

| step | ms |
|---|---|
| parse | 6.5 |
| shape and canonical text | 7 |
| `scalarRules` | 4.5 |
| effect order | 4.3 |
| type references | 2.7 |
| resolvers | 2.6 |
| other graph rules | 5 |

After the changes: parse 2.6–3.3, shape about 1, `scalarRules` 0.7. The F# open column varied from 38 to 72 ms across runs on the same input; within one run the Zig path is now ahead on both rows for Rhifill.

## Caching the emitter's output (2026-10-28)

runPure cached the Zig lowering per snapshot but ran the Zig emitter on every call. The emitter's output is now cached by emitter build (its source hash), entry and IR hash. Under `NYRWVY_ZIG_EMITTER_CHECK` a cached output is still compared with the F# emitter on every call. Merging the lowering and the emitter into one process would have saved only the spawn and the IR file round trip (about 1–2 ms on Rhifill), so it was dropped for this.

| | before | after |
|---|---|---|
| task-outcomes: runPure's emit, repeated | 4.4 | 0.1 |
| Rhifill: runPure's emit, repeated | 22.6 | 0.9 |

A repeated runPure of the same snapshot now skips both tools; the first run is unchanged (Rhifill: lowering 27.4, emitter 22.6). The remaining 0.9 ms is hashing the 1 MB IR.
