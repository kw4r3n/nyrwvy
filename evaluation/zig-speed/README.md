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
