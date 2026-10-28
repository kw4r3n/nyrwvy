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

## Zig emitter hotspots (2026-10-28)

For Rhifill's whole program (a 1 MB IR), the emitter spent about 8 ms parsing JSON, 3 ms on instruction lines, 3.5 ms declaring values and 2.5 ms on the rest of each function body. The changes, with outputs unchanged:
- a direct JSON reader that builds the same `std.json.Value`, giving each object its exact capacity and keeping strings without escapes as slices of the input; the input's UTF-8 is checked once;
- value and block identifiers memoised by ID, and built with their capacity reserved;
- body lines appended part by part instead of joined first;
- values deduplicated with a set instead of a quadratic scan.

| | before | after |
|---|---|---|
| task-outcomes: emitter | 4.4 | 3.6 |
| task-outcomes: to Zig source | 10.1 | 9.2 |
| Rhifill: emitter | 22.7 | 14.2 |
| Rhifill: to Zig source | 54.1 | 44.4 |

The JSON parse went from about 8 ms to 4 ms. Old and new emitters gave byte-identical output on 166 IRs: the whole programs and every entry of Rhifill and the repository examples. The extended emitter check compares against the F# emitter.

## A resolved snapshot validated once (2026-10-28)

Opening a resolved snapshot (one with a dependency sidecar, read by F#) validated the merged model twice: the reader validated it, and the workspace validated a second merge of the same two snapshots. The reader now keeps the merged model it validated, and `resolveDependency` of the same local snapshot and dependency object returns it. The `ZigSpeed` row "Rhifill resolved open" times `openReadOnly` on the resolved workspace itself; three interleaved runs each:

| | before | after |
|---|---|---|
| Rhifill resolved open | 51.2, 57.0, 52.5 | 36.3, 36.7, 33.5 |

This is now faster than opening Rhifill's merged model as a plain snapshot (about 60 ms on the Zig path, 50–70 ms on F#).

## The writer's JSON of a validated snapshot counts as validated (2026-10-28)

runPure passed `--validated` only for bytes the Zig validator accepted. The writer's JSON of a snapshot F# validated now counts too. This covers a resolved snapshot, which F# reads, and a proposal's candidate. On Rhifill's merged model, the whole lowering took 45.1 ms with `model-reader --execution-ir` and 27.5 ms with `--execution-ir --validated` (median of 13, with the same IR). That is the saving on the first runPure of a resolved Rhifill. The check modes still run without the flag.

## Building the F# model while the Zig validator runs (2026-10-28)

On the Zig-decided read, the F# Snapshot was built from the JSON only after `model-reader --validate` returned. The build now starts on its own thread (with an 8 MB stack, as the reader recurses) as soon as the JSON is known. The verdict then decides whether the built model is used. Under the reader check the build stays `readUnvalidated`, as before. Three interleaved runs each, ms:

| | before | after | F# only |
|---|---|---|---|
| task-outcomes: open, Zig-decided | 11.5, 11.7, 11.5 | 7.7, 7.4, 7.4 | 11.1–11.4 |
| Rhifill merged model: open, Zig-decided | 59.0, 54.9, 59.8 | 41.5, 42.0, 35.2 | 44.3–83.3 |

The Zig-decided open is now faster than the F# reader on both inputs. A resolved snapshot still reads with F# (31–34 ms for Rhifill; this change does not touch it).

## A resolved snapshot validated in Zig (2026-10-28)

Under the standing rule (Zig only where it is faster), a resolved JSON snapshot's merged model is now validated by `model-reader --validate --dependency`. F# builds the local model and checks the marker at the same time (docs/decisions/zig-tools.md). Three interleaved runs each, ms:

| | before (overlapped read, F# validates the merged model) | after (Zig validates it) |
|---|---|---|
| Rhifill resolved open | 33.5, 33.0, 31.2 | 30.2, 29.7, 30.1 |

## A resolved record read in Zig (2026-10-28)

A resolved `.nyrec` read with F# only. It now goes through `snapshot-reader --dependency`, then `model-reader --validate --dependency`, as a resolved JSON snapshot does. The row "Rhifill .nyrec open" opens Rhifill's resolved workspace transcoded to records (`RecordCodec.ofJson`), with the same sidecar. Three runs each, ms (another benchmark ran on the machine at the same time):

| | Zig route | F# reader |
|---|---|---|
| Rhifill .nyrec open | 43.3, 43.2, 48.7 | 54.3, 54.6, 55.2 |
| Rhifill resolved open (JSON, unchanged) | 32.3, 30.9, 32.0 | 27.7, 30.5, 29.8 |

The JSON row now also has the F# reader; in these runs it was not slower than the Zig route (#542 measured 30 against 33 ms). That route is unchanged here.

Rerun with nothing else running (1-minute load about 4, falling from 16), ms:

| | Zig route | F# reader |
|---|---|---|
| Rhifill .nyrec open | 44.1, 43.3, 44.5 | 55.3, 57.4, 54.9 |
| Rhifill resolved open (JSON) | 31.7, 30.7, 31.2 | 27.7, 28.1, 29.2 |

The `.nyrec` result holds. On resolved JSON the F# reader (`NYRWVY_SNAPSHOT_READER=fsharp`) is 2–4 ms faster in every run, so this is not noise. #542's "before" column was the overlapped read with F# validating the merged model, not the F# reader alone. That path is left as is here and reported for a decision.
