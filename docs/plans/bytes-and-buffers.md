# Bytes and in-place buffer updates (design note)

**Status:** Accepted by the owner on 2026-10-26, with the three questions answered as proposed (see the end). Implementation in progress. Decision 1 (the type, operations, codecs and run values, copying everywhere) is implemented: [decision record](../decisions/bytes-domain.md).

**Date:** 2026-10-26

## Context

Programs ported to Nyrwvy keep two kinds of work in the host because the language has no byte data and no cheap update of a large value:

- **Raw bytes.** PTY output, file and socket contents, protocol frames. Today the only form is a String holding one byte per code point (the latin-1 form `stringEncodeUtf8` returns and `stringDecodeUtf8Lossy` reads). It has no byte type, no indexing by byte, and every edit builds a new string. The [exact-bit note](../experiments/bit-representation.md) lists byte strings as outside the subset.
- **Buffers updated many times.** A terminal's cell grid, a ring buffer, a decoder's output. Values are immutable ([semantic independence](../concepts/semantic-independence.md#observable-behavior-and-backend-freedom)), so each `ListSetAt` copies a path of the persistent vector.

snap (the terminal built on Nyrwvy) is the first consumer that hits both. Its host boundary note keeps PTY bytes, the cell grid and drawing in C# for exactly these reasons.

## Evidence

A .NET 10 Release benchmark ([evaluation/bytes-buffers](../../evaluation/bytes-buffers/README.md)) runs the emitted runtime's `NyList` and `NyStr` against `byte[]`/`long[]`, on snap-sized work. Times are per operation after warm-up; allocation is `GC.GetAllocatedBytesForCurrentThread` per operation.

| Workload | Today | In place | In place after one copy |
| --- | --- | --- | --- |
| One frame: set all 12,780 cells (213×60) | `List<Int64>` `SetAt`: 4.40 ms, 11.75 MB | 0.011 ms, 0 B | 0.046 ms, 0.10 MB |
| Build 64 KiB one byte at a time | `List<Int64>` `AppendItem`: 1.33 ms, 5.2 MB; latin-1 `String` concat: 4.33 ms, 7.3 MB | 0.034 ms, 0 B | 0.034 ms, 64 KiB |
| Read every byte of 64 KiB | `List<Int64>` `ItemAt`: 0.39 ms | 0.030 ms | — |

At 60 frames per second the first row is 700 MB/s of garbage and a quarter of the frame budget. One copy per call (the last column) costs a hundredth of that. This meets the roadmap's evidence rule: a consumer exists and its workload is blocked (snap keeps the grid in C# because of it).

## Decision 1: one new value type, `Bytes`

`Bytes` is an immutable value: a finite sequence of integers 0..255. It is equatable (by length, then byte by byte) and has no identity. Every operation is total.

| Operation | Result | Rule |
| --- | --- | --- |
| `BytesEmpty` | `Bytes` | length 0 |
| `BytesFill(length, value)` | `Bytes` | `length` items of `value`; a negative length gives empty |
| `BytesLength(b)` | `Int64` | |
| `BytesGet(b, index)` | `Option<Int64>` | `None` outside `[0, length)`, as `ListItemAt` |
| `BytesSet(b, index, value)` | `Bytes` | unchanged outside `[0, length)`, as `ListSetAt` |
| `BytesAppend(b, value)` | `Bytes` | one more item |
| `BytesConcat(left, right)`, `BytesSlice(b, start, end)` | `Bytes` | bounds clamp, as `StringSlice` and `ListSlice` |
| `BytesFromLatin1(text)`, `BytesToLatin1(b)` | `Bytes`, `String` | the existing one-byte-per-code-point form; a code point above U+00FF reads as 0xFF, as `StringDecodeUtf8Lossy` does |

A stored value outside 0..255 keeps its low 8 bits (value mod 256), the rule `Bits32FromInt64` uses for 32 bits. Int64 is the element type at the interface: no `Bits8` domain is added (the exact-bit note asks for evidence per width, and nothing here does bit work on single bytes).

Index rules follow the List operations, not the real-time note's floor-modulo preference: that preference is argued from ring buffers, and for byte strings an out-of-range index is a bug that modulo would turn into a silent wrong read. A ring buffer writes `Int64FloorModulo` itself.

`StringEncodeUtf8` and `StringDecodeUtf8Lossy` keep their String forms; programs that use them do not change. A UTF-8 pair taking `Bytes` can be added later beside them.

**Rejected: a generic `Buffer<T>`.** Byte data is the measured need; the DSP slice of the real-time note needs `Float64` buffers with the same update rule, and can add its own type then. A generic buffer would add type-parameter and equality questions no consumer has asked.

## Decision 2: update in place when nothing else can see the old value

Semantics stay value semantics: `BytesSet` and `BytesAppend` return a new value and every other holder keeps the old one. In-place writing is a backend choice the language makes unobservable (the semantic-independence table already lists "copying or structural sharing of values" as backend freedom). The question is when a backend may skip the copy.

A body-local "no use after update" check (the real-time note's option A) is not enough outside processors: a `Bytes` parameter may still be held by the caller, and a value stored earlier into a List or Product is still reachable from it. The rule therefore has a static half and a runtime half.

**Static (computed while lowering to Execution IR).** An update node writes in place only when every other use of its input value is a *read* (`BytesGet`, `BytesLength`, `BytesToLatin1`, `BytesSlice`, `BytesConcat`, `ValueEqual`) and lowering schedules all of them before the update. Lowering already orders nodes with a scheduler and already moved one node for [tail self-calls](../decisions/tail-self-calls.md); the update waits until its input's other readers are emitted. A reader that depends on the update's own result (comparing the old value with the new one, say) makes that order impossible; the update then copies. The program is legal, so it is not rejected, but review shows it as a derived fact beside the function (`copiedUpdates`: the update node and the reader that forced the copy), the way body effect requirements are shown, so an unintended copy is visible. Any other use (a call argument, a constructor field, a callback capture, a capability argument, a `TaskStart` context) *escapes* the value: updates of an escaped value always copy.

**Runtime (one copy per invocation).** A `Bytes` object carries an owner token. Each function invocation has a token; a tail self-call keeps it, since it reuses the invocation. An in-place-eligible update writes in place only if the object's owner is the current invocation; otherwise it copies once and the copy is owned. Values that arrive as parameters, come back from calls, or are read from data structures are owned by someone else, so the first update copies and later updates in the same invocation (including later turns of a tail-recursive loop) write in place. At an escaping use the emitted code clears the owner, so a buffer stored into a list and then updated later in the loop is copied, never changed under the list.

This is the "one copy per external call" column of the table. It needs no ownership in the language (Q-012 stays deferred), no new review surface, and no annotation in programs. The cost of a non-linear program is the copy it would pay today, once per update instead of a path copy.

`runPure` and other interpreters may ignore all of this and copy on every update; results are identical.

## Representation and persistence

- C#: `NyBytes` holds `byte[]`, a length (capacity grows by doubling for `BytesAppend`) and the owner token. Equality and hash by content.
- Run values: `{ "kind": "bytes", "hex": "00ff7f" }`; the plain JSON form is the hex string.
- No literal node (see the owner decisions); Execution IR and Typed Semantic IR gain the new operation nodes, as `Bits32` did.

## First slice

1. `Bytes` type, the operations above, equality, codecs, wire forms, recipe kinds, viewer, interpreter (copying).
2. C# backend: `NyBytes` with the owner token, the static in-place fact in lowering, and the `copiedUpdates` review fact.
3. Tests per operation (as `Bits32Tests.fs`), in-place tests that observe aliasing (a value stored in a List, then updated in a loop, must not change the List), and the benchmark rerun through emitted code.

The footprint is that of `Bits32` (commit `f9dc39a`: 48 files across Foundation, Validation, codecs, Execution IR, the emitter, PureRun and wire, recipe reference and viewer).

Not in the slice: UTF-8 operations on `Bytes`, `Float64` buffers, bulk operations (`BytesIndexOf`, copy-into), and moving snap's grid. snap's PTY path and cell grid are the acceptance consumer after the slice lands.

## Owner decisions (2026-10-26)

1. A forced copy is a review fact (`copiedUpdates`), not a validation error.
2. A stored value outside 0..255 keeps its low 8 bits.
3. Run values write bytes as hex.

`Bytes` has no literal node in the first slice, as `Bits32` has none: a constant is `BytesFromLatin1` of a String literal. The schema bumps for a literal are therefore deferred.
