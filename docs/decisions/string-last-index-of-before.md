# StringLastIndexOfBefore

- **Status:** Accepted
- **Date:** 2026-10-19
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 71

[String search](string-search-and-affixes.md) (order 18) gave `StringIndexOf(text, substring, from)` a start bound, so a forward scan over every occurrence passes the previous match's end as the next `from`. `StringLastIndexOf(text, substring)` has no bound, so the backward scan Python writes as `text.rfind(sub, 0, end)` took a `StringSlice` per step, a copy of the prefix each time, or a forward scan kept in a fold. The owner chose on 2026-10-19 to add the bound under the [warranted-operation rule](../plans/post-m2-roadmap.md#selection-rule-from-here).

## Decision

**`StringLastIndexOfBefore(value, substring, end): Option<Int64>`** is Python `text.rfind(sub, 0, end)` with `-1` as `None`: the code-point position of the last ordinal occurrence of `substring` that ends at or before `end`. `end` is clamped to `[0, StringLength(value)]` like a `StringSlice` bound and `StringIndexOf`'s `from` (negative is 0, not counted from the end), an empty substring is found at the clamped `end`, and a match never starts or ends inside a surrogate pair. It never fails. `StringLastIndexOfBefore(text, sub, StringLength(text))` is `StringLastIndexOf(text, sub)`.

**A separate operation, not an optional operand.** `StringIndexOf` was designed with `from` from the start, so there is no precedent for an optional operand, and the semantic graph has none: a node's operands are node IDs, not options with defaults. Giving `StringLastIndexOf` a third operand would change the shape of every persisted node of that kind and need a migration that invents a `StringLength` node per occurrence; a separate kind leaves existing graphs and the two-operand kind untouched, the way `StringSpanBack(value, set, end)` stands beside `StringSpan(value, set, from)`. The name says what the bound is.

The fixed-signature row `stringLastIndexOfBefore` (wire `stringLastIndexOfBefore` with `value`, `substring`, `end`; Nyason `string-last-index-of-before`; persisted `valueNodeId`, `substringNodeId`, `endNodeId`) is checked from the `OperationSignatures` table, and the `end` operand carries the `SliceEnd` dependency role, as `StringSpanBack`'s does.

## Reason

- **Backward scans without copies.** Each step is one search over the original text instead of a slice; the match position is the next `end`.
- **Python's bound.** `rfind(sub, start, end)` bounds the match to `text[start:end]`, so a match must end at or before `end`; the clamping rule is the one every other Nyrwvy position follows.
- **Only the end bound.** A start bound on a backward search (`rfind(sub, start, end)` with `start > 0`) has not been needed; `StringIndexOf` has only `from` for the same reason.

## Consequences

- It follows `StringSpanBack` through every layer: validation (`StringIntrinsicTypeMismatch` in a persisted graph, `PureExpressionOperandTypeMismatch` from a recipe), both codecs, typed semantic IR, Execution IR, the C# backend (`NyText.LastIndexOfBefore`, which maps the clamped `end` to a UTF-16 offset and shares the boundary-checking backward search with `LastIndexOf`), wire recipes and the recipe reference. A snapshot that uses it is written at snapshot JSON 20 / Nyason `nayamnyam.snapshot.v16`.
- `StringLastIndexOfBeforeTests` checks a Python parity table under `runPure`, the clamping, code-point positions past U+FFFF and a backward scan over every occurrence.

## Revisit conditions

- **A start bound for the backward search:** a consumer needs `rfind(sub, start, end)` with a nonzero `start`.
- **A `from` on `StringLastIndexOf` itself:** if optional operands ever enter the graph model, the two kinds could merge; until then the pair stands.
