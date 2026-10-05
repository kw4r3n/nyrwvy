# The Bits32 domain

- **Status:** Accepted
- **Date:** 2026-10-01
- **Roadmap:** [post-M2 findings from the 2026-10-01 probe 12](../plans/post-m2-roadmap.md#findings-from-the-2026-10-01-probe-12), order 30

This decision settles order 30 of the post-M2 queue: fixed-width bit operations. [Probe 12](../verification/rrawrlet-slice-probe12-20261001.md) ported rrawrlet's `hashlib.sha256(...).hexdigest()` with Int64 arithmetic. XOR, AND and NOT have no arithmetic form, so 7 functions and 138 nodes emulated them with a 32-level self-recursion, one bit per call. That emulation was half of the digest's run time, the digest was about 7,000 times slower than `hashlib`, and a hashed file above about 1.5 MB exceeded the 5 s run limit. The [bit-representation experiment](../experiments/bit-representation.md) had already set the direction: a separate fixed-width bit domain, not masks on `Int64`, with only the widths and operations a concrete workflow needs. The implemented surface is described by the [language subset](../reference/language-subset.md#bits32), the [expression authoring specification](../specs/expression-authoring.md#bits32), the [C# backend](../specs/csharp-backend.md) and the [agent authoring session](../specs/agent-authoring-session.md), which remain the authority.

## Decision

**A primitive type `Bits32`.** It is exactly 32 bits with no numeric meaning. It is equatable, because it has no NaN, so `ValueEqual` compares two values bit for bit. It has no literal and no ordering. The type reference is `{"kind":"primitive","primitive":"bits32"}`; snapshot JSON writes `bits32`, Nyason `primitive-bits32`, and the IR serializers `primitiveBits32`. The C# backend maps it to `uint`.

**Ten total operations.** None of them fails:

| Operation | Meaning |
| --- | --- |
| `Bits32FromInt64(value: Int64): Bits32` | The low 32 bits of the two's complement form (`value mod 2^32`) |
| `Bits32ToInt64(value: Bits32): Int64` | The bits read unsigned, `0..4294967295` |
| `Bits32Xor`, `Bits32And`, `Bits32Or` `(left, right): Bits32` | Bitwise |
| `Bits32Not(value): Bits32` | Bitwise complement |
| `Bits32Add(left, right): Bits32` | Addition modulo `2^32` |
| `Bits32RotateRight(value, amount: Int64): Bits32` | Rotation right by `amount` floor-modulo 32; a negative amount rotates left |
| `Bits32ShiftRight(value, amount: Int64): Bits32`, `Bits32ShiftLeft(...)` | Logical shifts; `amount <= 0` gives the value, `amount >= 32` gives zero |

Constants are `Bits32FromInt64` of an `Int64Literal`. An operand of the wrong type reports `PureExpressionOperandTypeMismatch` in recipes and `Bits32OperandTypeMismatch` in persisted graphs. The rotation and shift amount is reviewed under a new `Amount` value-dependency role.

**Run values.** `runPure` and `runPureByName` carry Bits32 as `{ "kind": "bits32", "value": ... }`, with the value encoded like an Int64 value: a JSON integer or exact decimal string in requests and a decimal string in responses, here the unsigned value `0..4294967295`. Anything outside that range is a protocol error; input is never wrapped. Like Float64 and Unit, Bits32 has no flat container form.

**No schema version bump.** Like earlier node kinds (`Int64FloorDivide`, `Int64ToFloat64`, `Float64ToString`), the new primitive and node kinds are closed-set additions to snapshot JSON 9–12, Nyason, Typed Semantic IR and Execution IR. A snapshot that does not use Bits32 keeps its exact bytes and fingerprint. A reader that predates Bits32 rejects a snapshot that uses it as an unsupported primitive or an unknown node kind, so it fails closed and never misreads one.

## Reason

**A separate domain, not masks on Int64.** With masks, every Int64 addition in a hash would need a `mod 2^32` beside it, and every reader would have to check that it is there. A value would silently mean "a signed integer" in one place and "32 bits" in the next. A separate type makes the width and the wrapping part of the type: `Bits32Add` cannot overflow into bit 32, and passing a Bits32 where an Int64 is expected is a type error, not a reinterpretation. The two explicit conversions are where the meaning changes, and review sees them.

**Only 32 bits, and only these operations.** SHA-256 is defined on 32-bit words and needs XOR, AND, NOT, rotate right, logical shift right and addition modulo `2^32`. Those are the core. `Or` and `ShiftLeft` are the obvious counterparts: byte packing (`cur << 8 | byte`) uses both, and they cost one emitter line each. Nothing else was needed by the port.

**Total amounts.** Every Int64 is a valid amount, so no operation needs a failure path. A rotation by `n` and by `n + 32` are the same function of the bits, so the rotation amount is taken floor-modulo 32; a negative amount then rotates left, which is what rotating right by a negative amount means. A shift has no such period: shifting 32 bits right by 32 leaves nothing. C# and Java mask the shift count to five bits, so `x >> 32` is `x`, and C leaves it undefined; both surprise a reader of a hash. The subset instead says what the bits do: an amount `>= 32` shifts every bit out and gives zero, and an amount `<= 0` shifts nothing. The emitter spells those bounds out rather than relying on the host's masking.

**Unsigned conversion to Int64.** SHA-256 output, byte extraction and hex printing read words as unsigned. A signed reading would make `Bits32ToInt64(Bits32FromInt64(x))` differ from `x mod 2^32` for half of all words. Every 32-bit unsigned value fits in an Int64, so the conversion is exact.

**No literal.** A literal would be another node kind with its own codec, wire and viewer form, only to spell a constant that `Bits32FromInt64(Int64Literal)` already spells in two nodes. The port's constants are 8 initial hash words and 64 round constants; the round constants stay an Int64 list and are converted where used.

**Measured.** The Bits32 port of probe 12's digest is 17 functions and 547 nodes, counted as probe 12 counted its 17 and 622, and
 the bit functions shrink from 7 functions and 138 nodes to 6 and 63. It hashes 1.09 MB in 0.44–0.52 s and 17.5 MB in 2.4 s per run, against 3.4 s and a timeout at 2.2 MB for the Int64 port on the same machine and build: about 20 times the throughput. See the [verification record](../verification/bits32-20261001.md).

## Consequences

- The digest is no longer the limit on a hashed file: about 0.12 s per MB after the fixed per-run cost, so 17.5 MB fits in one run. The 5 s run limit, not the arithmetic, now bounds it.
- `Bits32` joins String, Int64, Bool and Unit as an equatable leaf, and it nests through List, Option, Result, Product and Union like any other value.
- Frozen evaluation protocols gain the primitive name `Bits32` only because their type printers match every primitive; their corpora are unchanged.
- Other widths, byte strings, `Float64ToBits64`/`Float64FromBits64` reinterpretation and a generic `Bits<N>` stay out, as the experiment recommends.

## Revisit conditions

- A real workflow needs another width (for example `Bits64` for SHA-512 or a 64-bit hash, or `Bits8` for byte-level protocols): add that width as its own domain with only the operations the workflow uses, following this one.
- A workflow needs exact Float64 payload bits: that is the case the experiment names for `Float64ToBits64`/`Float64FromBits64`, not for widening Bits32.
- Bits32 constants become a large share of a real program's nodes, so that the two-node spelling is a measured cost: reconsider a literal.
- A workflow needs a signed reading, bit counting (`PopCount`, leading zeros) or byte order conversion: add the one operation with its consumer, not a family.
