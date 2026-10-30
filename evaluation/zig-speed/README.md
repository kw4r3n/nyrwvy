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

## Where an AuthoringSession spends its time on Rhifill (2026-10-28)

Input for choosing between stopping the Zig port at the toolchain (reader, validator, lowering, emitter) and porting the whole session to a long-running Zig process (docs/decisions/zig-tools.md). The test `ZigSpeed: AuthoringSession ops on Rhifill` copies the resolved workspace and its sidecars (not the lock) into a temporary directory, so commit writes only the copy. It skips when `NYRWVY_SPEED_RHIFILL` is unset.

How each row is measured:
- **host spawn to status reply:** `dotnet Nyrwvy.AuthoringSession.dll open --snapshot <copy> --read-only`, as `tools/nyrwvy-agent/authoring.ts` launches a read session, from `Process.Start` to the first `status` reply on stdout. Median of 9 spawns after one, with the OS cache warm. "warm-up off" sets `NYRWVY_RUNPURE_WARMUP=0`.
- **host usage exit:** the same host with no arguments, which prints its usage and exits, so this is the runtime start alone.
- **in-process openReadOnly:** as in "Rhifill resolved open".
- **Requests:** on one `openExisting` session through `AuthoringProtocol.executeLine`, the per-line entry the host's `serve` loop calls (the host without stdio).
- **runPure:** Rhifill's case `actionName-focus_left`. Rhifill declares only the `csharp` backend, and the Zig backend refuses its capability operation types, so no Zig tool runs here. The rows:
  - "warm-up" is `PureRun.warm` once, which the host starts in the background at launch;
  - "first" is one measurement (C# emit and compile);
  - "repeated" is the median, which takes the compiled assembly from the in-process cache.
- **simulate and commit:** each of 10 rounds proposes a new function with one literal (`simulateCreateFunctionByName`) and commits it. The first round is dropped and the median of the other 9 is taken, so the copy grows by one small function a round.

Seven runs, ms. The "warm-up off" and "usage exit" rows were added after the first runs, so those cells are empty. Run 3 ran while another session's tests loaded the machine, at a load average of about 30.

| | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|
| host spawn to status reply | 609 | 656 | 775 | 658 | 657 | 658 | 623 |
| same, warm-up off | — | — | — | — | — | 673 | 625 |
| host usage exit | — | — | — | — | 30.2 | 29.0 | 29.5 |
| in-process openReadOnly | 33.1 | 31.2 | 45.8 | 32.4 | 32.8 | 35.0 | 31.2 |
| status | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| readFunction | 1.4 | 1.4 | 2.8 | 1.4 | 1.5 | 1.5 | 1.3 |
| search | 0.3 | 0.3 | 0.4 | 0.3 | 0.4 | 0.4 | 0.3 |
| runPure warm-up (once) | 1315 | 1301 | 1437 | 1333 | 1365 | 1321 | 1308 |
| runPure first (once) | 632 | 483 | 449 | 382 | 398 | 395 | 455 |
| runPure repeated | 11.6 | 7.2 | 8.0 | 9.1 | 6.7 | 7.2 | 8.1 |
| simulateCreateFunction | 64.0 | 44.7 | 50.2 | 47.8 | 45.1 | 44.1 | 43.1 |
| commit | 35.2 | 22.3 | 27.5 | 23.5 | 23.6 | 22.6 | 22.2 |

The reading:
- **Startup dominates.** A new host takes about 0.6 s to its first reply. Of that, the runtime start is about 30 ms and the open itself 31–35 ms when warm in-process, of which the Zig toolchain is at most the validator's part (see "A resolved snapshot validated in Zig" above). The remaining ~550 ms was not split further. It is most likely the session code running for the first time in a fresh process (JIT and type initialisation). The background runPure warm-up does not delay the first reply.
- **Per-request work is small.** Reads take 0–3 ms. A proposal with its review takes 43–64 ms, and its commit 22–35 ms. Neither split was measured between Zig tools and F# session logic.
- **runPure on Rhifill is C#-bound, not Zig-bound.** The first run of an entry takes 0.4–0.6 s (C# emit and compile), on top of a 1.3 s warm-up the host hides in the background. A repeat takes 7–12 ms. For comparison, the Zig route's lowering and emitter for the whole merged model take 27 and 13 ms (the rows above), but this workspace does not use them.

## Operation and mixed-batch host start (2026-10-31)

Candidate `610f91e`, measured by `ZigSpeed: AuthoringSession ops on Rhifill` on a copy of the current resolved `app/nyr/workspace.json` (456 KiB merged model). Each value is the median of 9 runs after one warm-up, on one machine; compilation is outside the timing. Host load varied during this run, so these are observations for this machine and run, not a stable performance ratio.

| | F# host | Zig host |
|---|---:|---:|
| resolved workspace: spawn to first status reply | 724.1 | 54.6 |
| merged model copy: spawn to first status reply | 582.1 | 57.0 |

With `NYRWVY_RUNPURE_WARMUP=0`, F#'s resolved-workspace start was 823.3 ms. Both `ZigSpeed` tests passed. These startup rows do not measure mixed-batch throughput.

Follow-up candidate `8954a0ee`: a second `ZigSpeed` run measured the same current resolved Rhifill copy (456 KiB merged). Each value is the median of 9 runs after one warm-up, on one machine; compilation is outside the timing. The task's builds and tests were not running during this measurement; that does not establish that the machine was otherwise idle.

| | F# host | Zig host |
|---|---:|---:|
| resolved workspace: spawn to first status reply | 611.6 | 49.6 |
| merged model copy: spawn to first status reply | 509.8 | 54.0 |

With `NYRWVY_RUNPURE_WARMUP=0`, F#'s resolved-workspace start was 603.5 ms. Both `ZigSpeed` tests passed. This remains a startup comparison; mixed-batch throughput was not measured.

Candidate `77338e1` (Step 5a), same Rhifill copy and startup measurement, median of 9 warm runs:

| | F# host | Zig host |
|---|---:|---:|
| resolved workspace: spawn to first status reply | 617.7 | 49.6 |
| merged model copy: spawn to first status reply | 524.8 | 55.8 |

With `NYRWVY_RUNPURE_WARMUP=0`, F#'s resolved-workspace start was 615.5 ms. Both `ZigSpeed` tests passed. This measures startup only, not migration throughput; it is one machine/run, with other Step 5 work potentially loading the machine.

## F# and Zig on equal terms (2026-10-28)

The same three tasks, from the same JSON files, in both languages:
- `validate` is `model-reader --validate` against `SnapshotCodec.deserialize`.
- `lower` is `model-reader --execution-ir` (it validates first) against deserialize, `TypedSemanticIr.project`, `ExecutionIr.lower` and `ExecutionIrSerializer.serialize`.
- `emit` is the Zig `emitter` on the IR JSON against `ZigBackend.runner` on the lowered program.

The F# side is `fsbench/` (`fsbench <task> <snapshot.json> [runs|check]`). The Zig side is the tools' `--bench <runs>` mode. Each run starts from a fresh arena and fresh globals, and must give the first run's output.

Outputs are byte-identical across languages and builds: the IR, the task-outcomes runner source, and the validation verdict. The one exception is Rhifill's `emit` under AOT; see below. On Rhifill both emitters do the whole pass and then reject the capability types (the earlier "Rhifill: emitter" rows measured the same rejection).

Builds:
- Zig: `zig build-exe main.zig -O ReleaseSafe`, as `ZigTools` builds it.
- F#, JIT: `dotnet publish evaluation/zig-speed/fsbench/FsBench.fsproj -c Release -r linux-x64 --self-contained false`.
- F#, ReadyToRun: the same plus `-p:PublishReadyToRun=true`.
- F#, Native AOT: `-p:PublishAot=true -p:TreatWarningsAsErrors=false -p:JsonSerializerIsReflectionEnabledByDefault=true` (see below).

Inputs: task-outcomes (61 KiB of model JSON) and Rhifill's merged model (453 KiB). hyperfine was not installed, so a Python loop times the runs. All numbers are in ms, from two runs at a 1-minute load of about 4.

**Cold:** one process per task, from spawn to exit, median of 15, interleaved. Spawning `/bin/true` takes 0.45. F#'s `emit` includes the lowering, because F# has no IR reader. Compare it with Zig's lower + emit: 10 ms for task-outcomes, 59 ms for Rhifill.

| | Zig | F# JIT | F# ReadyToRun | F# AOT |
|---|---|---|---|---|
| task-outcomes validate | 4.1 | 335 | 162 | 9.3 |
| task-outcomes lower | 7.2 | 505 | 232 | 17.4 |
| task-outcomes (lower+)emit | 2.9 | 510 | 231 | 15.5 |
| Rhifill validate | 25 | 478 | 282 | 47 |
| Rhifill lower | 46 | 765 | 450 | 90 |
| Rhifill (lower+)emit | 13.7 | 787 | 475 | 104 |

**Warm:** in-process, inputs in memory, median of N runs after N warm-up runs (N = 300 for task-outcomes, 100 for Rhifill). One warm-up run is not enough for the JIT. After 30 runs, F# validate of task-outcomes still took 8–9 ms, against 2.4 after 300. Zig's `emit` includes parsing the 1 MB IR JSON, about 4 ms on Rhifill; F#'s does not.

| | Zig | F# JIT | F# ReadyToRun | F# AOT |
|---|---|---|---|---|
| task-outcomes validate | 2.7 | 2.0–2.5 | 2.6–3.0 | 2.2–2.3 |
| task-outcomes lower | 5.3–5.5 | 5.5–5.9 | 5.6–5.7 | 5.5–5.7 |
| task-outcomes emit | 1.8 | 1.3 | 1.4–1.6 | 1.4 |
| Rhifill validate | 22.8–23.7 | 26.6–28.0 | 26.1–26.5 | 28.6–29.4 |
| Rhifill lower | 41.4–42.5 | 60.1–60.6 | 61.9–62.4 | 68.1–68.4 |
| Rhifill emit | 12.3–12.6 | 13.8–13.9 | 15.0–15.3 | 15.2 |

**AuthoringSession host start:** `open --snapshot <s> --read-only` to its first `status` reply, median of 9, same flags as above.

| | JIT | ReadyToRun | AOT |
|---|---|---|---|
| task-outcomes.nyrec | 493 | 276 | 49 |
| Rhifill (resolved workspace) | 741–756 | 501–513 | fails |

Reading:
- **Most of the gap is architecture, not language.** Warm, on the same work, the small input is a draw: F# is up to 25% faster on validate and emit, and the two are even on lower. On Rhifill, Zig is 1.15× faster on validate, 1.4× on lower, and 1.1× on emit (more once its JSON parse is taken out). Cold, the F# JIT process costs 330–790 ms where Zig costs 3–46 ms. Nearly all of that is runtime start and JIT, which the warm rows show is not the work itself.
- **ReadyToRun about halves cold F#. Native AOT brings it to about 2× Zig** (9–104 ms), and starts the host in 49 ms instead of 493.
- **Native AOT does not work as is.**
  - The build fails under `TreatWarningsAsErrors`: about 830 trim/AOT warnings, nearly all from `ExecutionIrSerializer` calling the reflection-based `JsonSerializer.Serialize<string>`. That call also throws at run time unless reflection serialization is switched back on.
  - F# `printf` needs generic code that AOT does not generate. `printfn "%.2f"` throws, so fsbench writes its numbers with `Console.WriteLine`.
  - `%A` prints records without their fields. Rhifill's emitter problems read "capability operation type Capability" without the ID, so their text differs from JIT. The record reader also builds its error placement from `%A` text (`RecordCodec.readModel`).
  - The AOT host opens task-outcomes but fails on Rhifill with `DependencyFailure: Invalid dependency manifest or source path.`, a `NotSupportedException` caught at `Session.fs:1054`. Not traced; probably the same `printf` family.

## The Zig session host's start (2026-10-28)

The row "start, merged model" of `ZigSpeed: AuthoringSession ops on Rhifill` spawns each host on Rhifill's merged model as a plain snapshot, read-only, and times spawn to the first `status` reply.  Median of 9 runs, three runs each, ms:

| | Zig host | F# host |
|---|---|---|
| start, merged model | 55.5, 55.0, 54.8 | 506.4, 502.5, 505.6 |

The Zig host reads, validates and fingerprints the model in that time: about 23 ms validation and about 30 ms for the two fingerprints (three passes of the canonical writer). The F# host on the resolved workspace itself takes 600–610 ms.

With dependencies (1c), the row "start, Zig host" times the Zig host on the resolved workspace itself, with its sidecar. Three runs, ms:

| | Zig host | F# host |
|---|---|---|
| start, resolved workspace | 47.9, 47.4, 47.2 | 605.2, 606.2, 605.4 |
| start, merged model | 51.2, 52.1, 51.7 | 501.0, 502.9, 500.9 |

The resolved workspace is the faster of the two for Zig: its local part is small, and the dependency (49 KiB) is read, validated and fingerprinted on its own.

