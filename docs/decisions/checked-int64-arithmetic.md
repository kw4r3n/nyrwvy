# Checked Int64 arithmetic

- **Status:** Accepted
- **Date:** 2026-10-03
- **Roadmap:** [post-M2 owner choice of 2026-10-03](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-03), order 40

This decision settles order 40 of the post-M2 queue. Checked Int64 arithmetic was an [open candidate](../plans/post-m2-roadmap.md#open-candidates) waiting on a further observation: only [probe 20](../verification/music-logic-probe20-20261001.md#costs-ranked-by-impact) needed it, writing checked add and multiply by hand (`addChk`, `mulChk`: 59 nodes) because wrapping is the only Int64 arithmetic. The owner chose on 2026-10-03 to add it on that one observation, under the selection rule's "or an owner choice"; it was not added under the [no-choice exception](standard-operations.md).

## Decision

**Three pure builtins, all `(Int64, Int64) -> Option<Int64>`:**

- `Int64CheckedAdd(left, right)`, `Int64CheckedSubtract(left, right)` and `Int64CheckedMultiply(left, right)` yield `Some` of the exact result of Python `left + right`, `left - right` and `left * right` when it lies in `[-2^63, 2^63)`, and `None` otherwise. They never fail.
- Wire `int64CheckedAdd`, `int64CheckedSubtract`, `int64CheckedMultiply` with the fields `left` and `right`; Nyason `int64-checked-add`, `int64-checked-subtract`, `int64-checked-multiply`.
- The C# backend computes the exact result in `System.Int128`, which holds any sum, difference or product of two Int64 values, and range-checks it. It does not use `checked` contexts or exceptions.

Probe 20 observed add and multiply. Subtract is included as the third member of the existing `Int64WrappingAdd`/`Subtract`/`Multiply` triple, so every wrapping operation has a checked form; it is the part the owner can trim without affecting the observed need.

## Reason

**Nothing about the result is left to guess.** Two's complement fixes the range, and the exact result is Python's: `None` marks exactly the inputs where Python's unbounded `int` and Int64 disagree. The one choice, how overflow is reported, follows existing rules: runtime operations never fail, and `Float64RoundToInt64`, `StringParseInt64` and `StringParseInt64Hex` already report a value outside Int64 as `None`.

**The hand-written form is costly and easy to get wrong.** Checked multiply by hand needs a division test or a split into halves, and the sign cases around `Int64.MinValue` (`MinValue * -1`, `MinValue - 1`) are exactly where a hand-written check fails. One node replaces about 30.

## Not included

- **Checked division and modulo.** `Int64FloorDivide` already gives `Int64.MinValue` for `Int64.MinValue / -1` and treats a zero divisor totally; whether a checked form should report those as `None` is a separate choice with no consumer.
- **Checked negate and absolute value.** `Int64CheckedSubtract(0, x)` gives checked negation; `Int64WrappingAbs` stays the only absolute value until a consumer needs `None` for `Int64.MinValue`.
- **Unbounded integers.** A bounded Int64 remains a deliberate subset choice; checked arithmetic detects overflow, it does not extend the range.

## Consequences

- `language-subset.md` no longer says checked arithmetic is outside the contract.
- Each operation is one more entry in the recipe reference for agents to find, and about 30 places in the semantic graph, codecs, IR, backend, viewer and wire forms.

## Revisit conditions

- A consumer needs checked division, negation or absolute value: decide what each reports for `Int64.MinValue` and a zero divisor.
- No program uses `Int64CheckedSubtract` by the next review of the builtin surface: consider whether the symmetry was worth it.
