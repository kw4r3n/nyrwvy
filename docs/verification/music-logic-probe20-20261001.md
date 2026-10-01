# Music-logic probe 20 — 2026-10-01

**Status:** Complete. Issue #283's just-intonation module was authored by name from a fresh snapshot: exact ratios, prime-exponent vectors over 2 to 13, the degree (親等) and the simple-interval rule, chords with a voicing rule, frequencies, cents with the nearest n-EDO step (41-EDO steps, minutes and seconds), the frequency of an EDO step, the total compromise score (総合妥協度) and a chord progression emitted through a `NoteSink` capability. The port matched a Python reference (`fractions`, `math`) on **3,400/3,400 cases**, including all 385 `NoteSink` calls in order. The only difference on the first full run was a bug in the reference, not in the port. **Nothing is blocked.** One candidate meets the issue's decision rule: converting a Float64 to an Int64 with Python's rounding, needed here for the step index and observed before in probe 15. The owner queued it, and it is now order 34 (see [After order 34](#after-order-34)).

This is the first probe outside rrawrlet's Python tooling. The numeric pressure is different: exact rational arithmetic at the edge of Int64, logarithms, powers and rounding to a step. `matchUnion`, `ProductUpdate`, Map and tail calls were not needed by the module.

**Date:** 2026-10-01

Baseline: nyrwvy `f9f6335` (main after #286, clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release` on .NET SDK 10.0.112. The reference ran on Python 3.11.15. The method was probe 19's: name-resolved requests over the `Nyrwvy.AuthoringSession` JSON-lines protocol, and `runPure` batches through a `runCases` entry. Probe 11's session client and recipe helpers were reused unchanged. The harness, cases, snapshot and transcript are in the project's shared files (`music-probe20/`) and are not committed.

## Scope

All seven items of #283 and two of the additions from the owner's comment (41-EDO errors in steps, minutes and seconds; the total compromise score). The equave is a parameter, with the octave as the default.

Not done: **harmonym naming** and the **progression notation parser**. Both need the naming and fusion rules from LΛMPLIGHT's term dictionary, and the saved pages were not in the project files. They can be a follow-up probe when the pages are available.

Where the issue leaves a rule open, the port and the reference use the same choice (these are assumptions, not the theory as written):

- **Range.** A ratio's numerator and denominator stay in the symmetric range ±(2^63 − 1); anything else is `overflow`. Python's `Fraction` is unbounded, so the reference checks the same range.
- **Degree.** The sum of the absolute exponents. With octave equivalence on, dimension 1 (the prime 2) is not counted. An interval is simple when its degree is at most 2.
- **Voicing.** Each pitch is reduced to [1, 2) and raised by octaves until it is at least the given low bound and above the previous pitch. Each adjacent gap is then checked against 150 to 450 cents, a quarter octave ± 150.
- **Minutes and seconds.** One 41-EDO step is one minute (′); the error's seconds are `round(error_in_steps × 60)`.
- **Compromise score.** For a chord given as exponent vectors: for each dimension d from 2 to 6, the largest step error among the chord's vectors that use that dimension, divided by d, summed. "Maximum" is read as the maximum over the chord.
- **Progression.** Every event is computed first, then emitted in order as `NoteSink.note(time, pitch, duration, hz)` with ratios as `n/d` text. A failure emits nothing.

## What was authored

| Part | Functions / nodes |
| --- | --- |
| Types | 12 (`Ratio`, `Pair`, `VecState`, `FromVec`, `Event`, `ChordIn`, `ProgState`, `Split`, `Nearest`, and the harness's `Out`, `Case`, `Gaps`), 1 effect, `NoteSink` with 1 operation |
| 1. Ratio: gcd, normalise, checked multiply and add, multiply, divide, add, compare, equave reduction | 16 / 374 |
| 2. Prime-exponent vectors, both directions | 7 / 148 |
| 3. Degree and the simple rule | 3 / 39 |
| 4. Chords and voicing | 6 / 83 |
| 5. Frequency of a ratio | 1 / 9 |
| 6. `log2` and cents | 4 / 120 |
| 6. Float64 floor and round-half-even to Int64 | 3 / 95 |
| 6. Nearest EDO step with cents, steps and seconds | 1 / 28 |
| 6. `exp2` and the frequency of an EDO step | 5 / 72 |
| Total compromise score | 6 / 84 |
| 7. Progression and `NoteSink` | 6 / 101 |
| **Module total** | **58 / 1,153** |
| Harness: case dispatch and output text | 11 / 552 |

93 proposals. There were **two recipe rejections**, both from size limits:

- `exp2` with Horner's rule over 20 Taylor coefficients written inline was 42 nodes deep, over the limit of 32 (`StructuralRecipeDepthLimitExceeded`). It is now a `ListFold` over a coefficient list (`hornerStep`, `expCoeffs`). The 12-term `atanh` series in `lnNear1` fits inline.
- The harness's case dispatch had 399 nodes, over the limit of 256. It was split in two.

## Results

3,400 cases from `mkcases.py`. Text outputs (ratios, errors, vectors, steps, seconds, flags) compare exactly. Frequencies of a ratio compare exactly, since the port does the same two IEEE operations as Python. The values the port computes with its own `log2` and `exp2` compare with a tolerance.

| Group | What it covers | Result | Largest difference |
| --- | --- | --- | --- |
| `ratio` (411) | Every pairing of 24 special values (0, ±1, ±2^62, ±(2^63 − 1), −2^63, 2^53 + 1, 3^39, …) and random pairs | 411/411 | exact |
| `mul`, `div`, `add` (264 each) | All pairs of 12 fixed ratios at the range edges, and random ratios from small to full-range | 792/792 | exact |
| `cmp` (264) | The same fixed pairs; random pairs, equal pairs, and pairs 1/(2^63 − 1) apart | 264/264 | exact |
| `reduce` (240) | Equaves 2, 3, 3/2, 5/4, 81/80, 2^61, (2^63 − 1)/(2^63 − 2), 1, 1/2, −2; zero, negative and full-range inputs | 240/240 | exact |
| `vec`, `fromvec` (240, 133) | 13-smooth and non-smooth ratios; exponents up to the overflow edge (2^62, 3^39, 13^17), and a 7-element vector | 373/373 | exact |
| `degree` (240) | With and without octave equivalence; non-smooth ratios | 240/240 | exact |
| `chord` (240) | Roots, intervals and low bounds including negative and zero; voicings with gap flags | 240/240 | 1.1e-12 cents |
| `freq` (120) | Bases from 1e-300 to 1e300; full-range ratios | 120/120 | exact |
| `cents` (360) | 41-, 53-, 72-, 12-, 31- and 1-EDO; step, seconds, cents and both errors | 360/360 | 2.6e-12 cents |
| `edohz` (120) | Steps −150 to 150 | 120/120 | 2.2e-16 relative |
| `score` (120) | Chords of 1 to 5 vectors | 120/120 | 5.2e-14 |
| `prog` (120) | 1 to 4 chords; durations including one that overflows the time | 120/120, 385 calls | exact |
| **Total** | | **3,400/3,400** | |

The cents cases leave out values within 10^-6 of a rounding tie, where a last-bit difference in `log2` would decide the step. Wrong expected text, numbers just outside the tolerance and a changed call argument were all reported as failures, so the comparison is not vacuous.

**The first full run gave 3,399/3,400.** The difference was a reference bug: the Python `fromvec` used `zip`, which silently drops a seventh exponent, while the port reports `too many exponents`. The reference now rejects it too. The port did not change after its first full run.

## Scale

Warm `runPure` wall time of one case, including process start. Every row matched Python.

| Case | runPure | Python |
| --- | --- | --- |
| Reduce 1/3^39 into [1, 2) (62 doublings) | 0.27 s | 0.3 ms |
| Compare F92/F91 with F91/F90 (about 90 reciprocal steps) | 0.27 s | 0.03 ms |
| Cents and the 41-EDO step of (2^63 − 1)/1 | 0.25 s | 0.03 ms |
| Compromise score of 64 vectors | 0.32 s | 5 ms |
| Progression of 40 six-note chords (240 `NoteSink` calls) | 0.37 s | 2 ms |

Every recursion is bounded by the bit width (at most about 90 levels), so no case approaches a stack limit.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **Float64 to Int64 with rounding** | The step index is `round(cents × n / 1200)`, the seconds are `round(error × 60)`, and `exp2` needs `floor`. Written by hand as a 62-step binary search over `Int64ToFloat64` (`truncPos`), then `floorF` and `roundF` with Python's half-to-even rule: 3 functions, 95 nodes. Probe 15 converted Float64 seconds to milliseconds by hand, and probe 17 rounded with `round(x, 1)` | `Float64ToInt64` with Python's `round` (half to even) and `floor`, failing or returning `None` outside the Int64 range and for NaN or infinities. This meets the [Float64 decision](../decisions/float64-conversion-and-run-values.md)'s revisit condition (an index computed from a Float64) and the issue's rule (costly here, observed in probe 15). Done as order 34 |
| 2 | **No transcendental functions** (first observation) | `log2` (halving into [1, 2) and an `atanh` series: 4 functions, 120 nodes) and `exp2` (a Taylor series and powers of 2: 5 functions, 72 nodes). Both agree with libm to within about 1e-15 relative. The inline series hit the recipe depth limit once | None queued. A first observation. Bit-exact results across backends are the open question, and [#284](https://github.com/kw4r3n/nyrwvy/issues/284) asks it for DSP too |
| 3 | **Exact arithmetic at the Int64 edge** (first observation) | Checked multiply and add (`mulChk`, `addChk`: 59 nodes) because wrapping is the only arithmetic; a gcd over non-positive values because −2^63 has no positive counterpart (`gneg`, `nonPos`, `cancel`: 34 nodes); comparing two ratios without multiplying, by continued fractions (`cmpF`, `cmpR`: 55 nodes) | None. Related to the unbounded-`int` observations of probes 18 and 19, but a different need: overflow detection rather than a bigger integer |

Not needed: a `matchUnion` default arm (the module has no union), `ProductUpdate` (only the harness used it), Map, tail calls as loops (every recursion is bounded by the bit width) and UTF-8 decoding.

## Candidate hit counts after probe 20

| Candidate | Probes |
| --- | --- |
| `matchUnion` default arm (needs a decision) | 7, 10, 17, 18, 19 (5) |
| Float64 to Int64 and rounding | 15 (milliseconds), 17 (`round(x, 1)`), 20 (step index); `round` and `floor` done as order 34, decimal rounding still a candidate |
| Unbounded `int` | 18, 19 (2, small) |
| Map | 16 strong, 13 weak |
| Tail self-call as a loop | 15 |
| Transcendental Float64 functions | 20 |
| Checked Int64 arithmetic | 20 |

## After order 34

The owner queued the first cost as order 34: [`Float64RoundToInt64` and `Float64FloorToInt64`](../decisions/float64-to-int64.md). `author.py <snapshot> order34` authors the same module with them in place of `truncPos`, `floorF` and `roundF`. Each of `floorF` and `roundF` becomes a 5-node wrapper that unwraps the `Option` (the values here are far inside the Int64 range). The module goes from 1,153 to 1,068 nodes, the rounding group from 95 to 10, and it matches the same 3,400/3,400 cases (`run-order34.txt`, `author_stats_order34.json`).

## Reproducing

The scripts are in the project's shared files under `music-probe20/`. `author.py <snapshot>` authors the module and writes `author_stats.json` with per-group counts. `ref.py` is the Python reference. `mkcases.py [out.json]` builds the 3,400 cases from it. `run_cases.py <snapshot> cases.json` runs them, and `scale.py <snapshot>` gives the scale rows. `nw.py` and `dsl9.py` are probe 11's client helpers, unchanged. Run the scripts with `PYTHONPATH=music-probe20` and `NW_BIN` set to the built `Nyrwvy.AuthoringSession`.
