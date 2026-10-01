# Real-time-safe processor declarations (design note)

**Status:** Proposed. A design note for [#284](https://github.com/kw4r3n/nyrwvy/issues/284). Nothing here is implemented or accepted; implementation waits for the [evidence trigger](#evidence-trigger) below, per the roadmap's [selection rule](post-m2-roadmap.md#selection-rule-from-here).

**Date:** 2026-10-01

## Context

The owner wants Nyrwvy to cover the host side of a music application eventually, not only the composition logic of [probe 20](../verification/music-logic-probe20-20261001.md) (#283). The host side has three layers:

| Layer | Fits today's model? |
| --- | --- |
| Transport and project state | Yes. Ordinary pure functions and Products, as in probes 3 to 20. |
| Audio and MIDI device access | Yes, as an external capability bound by the host, like `NoteSink` in probe 20. |
| Per-block DSP on the audio thread | No. See below. |

Per-block DSP runs inside a host callback that must return within one block period (128 samples at 48 kHz is about 2.7 ms) every time. Missing it once is an audible dropout. That rules out anything whose worst case is unbounded or that can wait on another thread: allocation that can trigger a garbage collection, locks, I/O and unbounded loops.

Today's subset cannot meet that, for reasons in the current [language subset](../reference/language-subset.md) and [C# backend](../specs/csharp-backend.md):

- Products and Union variants are emitted as `sealed record` classes, and `List<T>` and `String` are heap objects (`NyList<T>`, `NyStr`). Building any of them allocates.
- There is no mutable buffer. A delay line written as a `List<Float64>` is copied or re-appended on every write.
- There is no loop construct. Iteration is `ListFold` over a list that must first exist, or self-recursion, which is bounded only by the stack.
- There is no `Float32`, and no transcendental Float64 function: probe 20 wrote `log2` and `exp2` by hand (192 nodes).

## Idea

Do not make general Nyrwvy code real-time capable. Add a restricted declaration kind, called `Processor` here, that the validator proves real-time safe, and that lowers to allocation-free code. Ordinary functions keep their current meaning and emission unchanged.

A processor has the shape `(state, params, input) -> (state, output)` for one block:

- `state` is a Product of numeric fields and fixed-length buffers, created by the host once, outside the audio thread;
- `params` is a Product of numeric fields, the control values for this block;
- `input` and `output` are blocks: fixed-length sequences of Float64 samples, one per channel, with a length at most the processor's declared `maxBlockSize`.

The validator proves three facts, and review shows them as derived facts beside the declaration, the way [body effect requirements](../specs/body-effect-requirements.md) are shown:

1. **No allocation after initialization.** Every value reachable in the processor's body and its callees has a fixed size known at validation time.
2. **Bounded work.** The call graph from the processor is acyclic, and every loop has a bound derived from `maxBlockSize` or a buffer length. The validator can report the worst-case operation count per block as a number.
3. **No waiting.** No Effect, capability invocation, task operation or `ParallelAll` is reachable.

This fits Nyrwvy's model better than a source-language rule would. The semantic graph already holds every callee, type and node by identity, so "allocation reachable via `mix` → `ListConstruct`" is a query over validated facts, not a heuristic over C++ or Rust source.

## Questions

### 1. Mutation

Delay lines and wavetables need in-place updates. The [semantic-independence](../concepts/semantic-independence.md#observable-behavior-and-backend-freedom) note already fixes the observable rule: building an updated value leaves other holders with the old value, and changing shared state is only possible through a capability operation. A backend may choose copying or structural sharing. The options differ in who can observe the old value.

| Option | Meaning | Review | Cost |
| --- | --- | --- | --- |
| A. Unique state, value semantics | Buffers are values. `BufferSet(buffer, index, value)` returns a new buffer. Inside a processor, the validator rejects any use of a buffer after it was passed to `BufferSet` (a use-after-update check over one function body). Since no other holder can exist, the backend writes in place. | Unchanged: the program reads as pure value updates; in-place writing is a backend choice the rule makes unobservable. | One local linearity check. No general ownership system. |
| B. Host-owned buffer capability | A `DelayLine` capability with `read` and `write` operations, bound by the host. | Mutation is visible as a declared effect. | Contradicts fact 3: real-time safety would rest on the host's implementation, an asserted trust boundary rather than a derived fact. Every sample access is an interface call. |
| C. General ownership or borrowing | A core ownership model ([Q-012](../decisions/open-questions.md#q-012--what-ownershipresource-model-should-the-core-eventually-express)). | Large new review surface. | Far beyond this need. |

**Recommended: A.** It keeps value semantics, keeps real-time safety a derived fact, and needs only a check local to one body. It does not resolve Q-012; it is a bounded rule inside processors.

Indexing needs a total rule, because runtime operations never fail. Options: floor-modulo the index by the buffer length (a ring buffer, which is what delay lines and wavetables do anyway), clamp to the ends, or return `Option<Float64>`. **Recommended: floor-modulo**, because it is total, branch-free and the common case needs no extra nodes. This is a choice for the owner.

### 2. Numeric domains

**Float64 is enough for a first slice.** Devices usually exchange 32-bit samples, but the host converts at the boundary (`float` to `double` is exact; `double` to `float` rounds once). Many DSP libraries process in double internally. `Float32` becomes worth adding when a measured processor is limited by memory bandwidth or SIMD width, or when output must match a Float32 reference bit for bit.

**Transcendental functions** needed by typical DSP: `sin`/`cos` (oscillators, filter coefficients), `exp`/`log` (envelopes, decibels, pitch), `tanh` (saturation) and `pow` (frequency from pitch, which `exp2` covers). Probe 20 needed `log2` and `exp2` outside DSP too.

Bit-exact reproducibility across backends does not hold for a platform math library. .NET's `Math.Sin` and similar call the C runtime, and its documentation says results may differ between operating systems and architectures. IEEE 754 recommends but does not require correctly rounded transcendentals, and math libraries differ in the last bit. `runPure` matches Float64 bitwise ([decision](../decisions/float64-conversion-and-run-values.md)), so a platform result would make a run's expected value depend on the machine.

| Option | Reproducible | Cost |
| --- | --- | --- |
| Platform math library | No | Fastest; needs a new "within tolerance" semantic category that Nyrwvy does not have |
| Nyrwvy-defined algorithm: a fixed range reduction and polynomial using only IEEE basic operations, with documented accuracy (for example within 1 ulp) | Yes, on any backend that performs basic operations without contracting multiply-add (the C# JIT does not contract to FMA unless `Math.FusedMultiplyAdd` is called, as far as we know) | The algorithm becomes part of the specification; a few dozen operations per call |
| Correctly rounded functions (as in the CORE-MATH project) | Yes, and the result is the one any correct implementation gives | Larger and slower runtime code; worst cases need extra precision |

**Recommended: a Nyrwvy-defined algorithm**, decided as a general Float64 addition rather than a processor feature, since probe 20 needed it outside DSP. It would be its own decision record with the five fields of the selection rule. Correctly rounded functions remain the upgrade path if a consumer needs agreement with other systems rather than across Nyrwvy backends.

**Subnormals.** Audio code often enables flush-to-zero to avoid slow subnormal arithmetic. That changes IEEE 754 results, so a backend must not enable it silently. A processor that needs protection adds it in the program (a tiny offset or an explicit threshold), and the host must not change the floating-point mode of the audio thread.

### 3. Loops

A processor needs a loop over the samples of a block and over the taps of a filter. The candidates already recorded are different:

- probe 15's **tail self-call as a loop**: unbounded iteration in ordinary code, for an event loop;
- probe 13's **missing `while`**, written as `ListEffectFold` over a bound list;
- the **`ListRange` candidate** (probes 5, 7, 12), waiting for a choice about huge ranges.

An index fold, `IndexFold(count, seed, step)` that calls a pure `step(state, index)` for `index` in `0 .. count - 1`, would serve all of them without building a list. Outside processors `count` is any Int64 (`<= 0` returns the seed), and it is a total, bounded loop that the C# backend emits as a `for` loop. Inside processors `count` must be derived from `maxBlockSize`, a buffer length or a literal, so the validator can bound the work.

They share the lowering. Execution IR already has blocks with `Jump` arguments, and the C# emitter already emits blocks as labels and `goto`, so a back edge needs no new backend structure. Today's lowering does not produce back edges. They do not share semantics: an index fold is bounded by its count, while a tail-call loop is not. **Recommended:** design `IndexFold` once, for both, and treat the tail-call loop as a separate candidate. Adding `IndexFold` for ordinary code needs its own evidence or owner choice; the probe 13 and `ListRange` observations may already be enough.

### 4. Validation

A rejected processor must say what to change. Each diagnostic names the offending node by display name and stable ID, with the call path from the processor and the recipe path of the node, in the [order 19 style](post-m2-roadmap.md#queue-from-the-2026-09-29-probe-4):

| Code | Example text |
| --- | --- |
| `ProcessorAllocationReachable` | `Allocation reachable: 'voice' (id) calls 'mix' (id), whose body builds a List at body.whenTrue (listConstruct). Processors may use Float64, Int64, Bool, Bits32, Option and Result of those, numeric Products and buffers.` |
| `ProcessorUnboundedRecursion` | `'filter' (id) calls itself through 'stage' (id). Processor call graphs must be acyclic; use IndexFold.` |
| `ProcessorLoopUnbounded` | `IndexFold count at body.seed (int64Add) is not derived from maxBlockSize, a buffer length or a literal.` |
| `ProcessorEffectReachable` | `'log' (id) invokes capability 'Console' at body (capabilityInvoke). Processors cannot perform effects.` |
| `ProcessorBufferUsedAfterUpdate` | `Buffer 'delay' is read at body.arms[1] after BufferSet at body.input; use the updated buffer.` |

The diagnostic should report the first offending path for each callee, not every path, so a widely used helper does not produce hundreds of lines.

### 5. Backend

Allocation-free C# is achievable for the first slice: state as a `struct`, buffers as host-owned `double[]` passed as `Span<double>`, numeric Products used in processors as `readonly struct` twins (ordinary emission keeps `sealed record`, so existing generated interfaces do not change), `Option` and `Result` already `readonly record struct`, and `BufferSet` as an indexed store.

It is necessary but not sufficient. A .NET garbage collection triggered by **another** managed thread suspends every thread running managed code, including an audio thread that never allocates. A C# host therefore also has to keep the rest of the process quiet (low-latency GC modes, `GC.TryStartNoGCRegion`, or a separate audio process). Whether that meets the latency needs is a measurement, not a design question.

A second, native backend (C or Rust) may be needed later. The Execution IR is backend-neutral, and a processor's IR would give a native backend what it relies on:

- every type has a fixed size and layout known at emission, so state maps to a C struct;
- no managed value (List, String, function value, capability, task) is reachable;
- the call graph is acyclic and every loop is a bounded back edge;
- Float64 operations are IEEE basic operations or Nyrwvy-defined functions built from them, with no contraction.

**Recommended:** C# only for the first slice. Record the GC-suspension risk, and revisit a native backend only when a measured host misses deadlines because of collector pauses.

### 6. Host boundary

| Host provides | Nyrwvy provides |
| --- | --- |
| Device I/O and the audio callback; block scheduling | The per-block computation |
| Allocating state and buffers at initialization, off the audio thread, with lengths from the processor's declaration | The initial state values, as an ordinary pure function |
| Moving control changes from other threads to the audio thread (a lock-free queue) and passing the current values as `params` for each block | Parameter smoothing: it is per-sample arithmetic and should be reproducible |
| Sample-rate and block-size changes, by re-initializing | Everything that depends on them, computed from `params` |
| Converting between device sample formats and Float64 | — |
| MIDI input, delivered per block as a fixed-capacity event buffer | Mapping events to state changes |

Composition logic (probe 20) stays ordinary Nyrwvy code on a non-real-time thread and reaches the audio thread only through the host's queue.

## Recommended first slice

1. A `Processor` declaration over Float64 blocks with a declared `maxBlockSize` and channel counts, numeric `state` and `params` Products, and `Float64Buffer` fields whose lengths are fixed at initialization.
2. `BufferGet` (floor-modulo index), `BufferSet` (with the use-after-update check) and `BufferLength`.
3. `IndexFold`, bounded by `maxBlockSize`, a buffer length or a literal inside processors.
4. The three derived facts and the five diagnostics above, shown in review.
5. C# lowering to structs, spans and in-place stores, with `runPure` running a processor over given blocks so it is testable like any function.

Not in the first slice: Float32, transcendental functions (a separate Float64 decision), Unions inside processors, function values inside processors, multiple processors in a graph, and a native backend.

## Evidence trigger

Start implementation only when both hold:

1. **A consumer exists.** The music project commits to authoring at least one DSP unit in Nyrwvy (for example one voice: oscillator, filter and envelope), not only composition logic.
2. **An audio probe is blocked without it.** That unit, written in today's subset with `List<Float64>` blocks and run from a .NET host through the C# backend at a fixed block size, allocates in steady state (measured per block with `GC.GetAllocatedBytesForCurrentThread`) or misses its deadline, or cannot be written at all.

The transcendental functions can be decided earlier and separately: probe 20 is their first observation, and a second consumer (an audio probe or another program) meets the selection rule.

## Non-goals

- Implementing any construct in this note.
- Choosing an audio library for the host.
- Plugin hosting (VST3, CLAP).
- Real-time safety for ordinary functions, or a general ownership system.
