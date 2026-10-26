# The Bytes value type

- **Status:** Accepted
- **Date:** 2026-10-26
- **Design note:** [Bytes and in-place buffer updates](../plans/bytes-and-buffers.md), Decision 1 and the owner decisions

This record settles the first half of the design note: the `Bytes` type, its operations, codecs and run values, with copying semantics in every backend. In-place updates (Decision 2: the static fact in lowering, the owner token and the `copiedUpdates` review fact) are a later step. The implemented surface is described by the [language subset](../reference/language-subset.md#bytes), the [expression authoring specification](../specs/expression-authoring.md#bytes), the [C# backend](../specs/csharp-backend.md) and the [agent authoring session](../specs/agent-authoring-session.md), which remain the authority.

## Decision

**A primitive type `Bytes`.** An immutable, finite sequence of integers 0..255, equatable by length and then content, with no identity, ordering or literal. The type reference is `{"kind":"primitive","primitive":"bytes"}`; snapshot JSON and records write `bytes`, and the IR serializers `primitiveBytes`. A constant is `BytesFromLatin1` of a String literal (owner decision: no literal node, so no literal schema work).

**Ten total operations.** None of them fails:

| Operation | Meaning |
| --- | --- |
| `BytesEmpty: Bytes` | Length 0 |
| `BytesFill(length: Int64, value: Int64): Bytes` | `length` copies of `value`; a negative length gives empty |
| `BytesLength(value: Bytes): Int64` | |
| `BytesGet(bytes, index: Int64): Option<Int64>` | `None` outside `[0, length)`, as `ListItemAt` |
| `BytesSet(bytes, index: Int64, value: Int64): Bytes` | Unchanged outside `[0, length)`, as `ListSetAt` |
| `BytesAppend(bytes, value: Int64): Bytes` | One more byte |
| `BytesConcat(left, right): Bytes` | |
| `BytesSlice(bytes, start: Int64, end: Int64): Bytes` | Bounds clamp to `[0, length]`, `end <= start` gives empty, as `ListSlice` |
| `BytesFromLatin1(value: String): Bytes` | One byte per code point; above U+00FF gives `0xFF`, as `StringDecodeUtf8Lossy` reads |
| `BytesToLatin1(value: Bytes): String` | One code point U+0000..U+00FF per byte |

A stored Int64 keeps its low 8 bits (`value mod 256` of the two's complement form; owner decision 2), the rule `Bits32FromInt64` uses for 32 bits. Int64 is the element type at the interface; there is no `Bits8`. An operand of the wrong type reports `PureExpressionOperandTypeMismatch` in recipes and `BytesOperandTypeMismatch` in persisted graphs. Review reuses the List roles: `Source`, `Index`, `Item`, `SliceStart`, `SliceEnd`, and `LeftOperand`/`RightOperand` for `BytesFill` and `BytesConcat`.

**Operand names.** Recipes and records name the Bytes operand `bytes` (`bytesNodeId` in snapshot JSON), the stored Int64 `value`, and the rest `length`, `index`, `start`, `end`, `left` and `right`, so `BytesSet` reads `bytes`, `index`, `value` where `ListSetAt` reads `list`, `index`, `item`.

**Run values.** `{ "kind": "bytes", "hex": "00ff7f" }`, two hexadecimal digits per byte, lower case in responses and either case in requests (owner decision 3); the plain JSON form is the hex string. Like Bits32, Bytes has no flat container form. The runner carries a value as its latin-1 String, so a Bytes result shares the 65536-code-unit String budget.

**C# backend.** `NyBytes` is a sealed class over a `byte[]` and a length, with content equality and hash. Every update copies into a new array. The private constructor already separates length from array size, so the later step can add capacity and an owner token without changing the API the emitter calls.

**Schema version 23.** Unlike Bits32 (which predates per-construct gating), the type and its node kinds have a row in `SnapshotSchema.minimumVersions`: a snapshot that mentions the `Bytes` type anywhere or uses any Bytes node kind needs schema 23 (`nayamnyam.snapshot.v19`), now the current version. Typed Semantic IR and Execution IR are in-process and gain the ten node and instruction kinds without a version change, as `ListScan` did.

## Not decided here

In-place updates and `copiedUpdates`, UTF-8 operations on Bytes, bulk operations (`BytesIndexOf`, copy-into) and `Float64` buffers, as the design note lists. `BytesFill` with a length above the .NET array limit fails with `OutOfMemoryException` in the C# runtime, as a String that large does; the language rule stays total.
