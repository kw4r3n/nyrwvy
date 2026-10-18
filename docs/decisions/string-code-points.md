# String code points and the recursion depth bound

- **Status:** Accepted
- **Date:** 2026-09-29
- **Roadmap:** [post-M2 queue from the 2026-09-29 probe 4](../plans/post-m2-roadmap.md#queue-from-the-2026-09-29-probe-4), order 17

This decision settles order 17 of the post-M2 queue: how a program iterates text. [Probe 4](../verification/rrawrlet-slice-probes-summary.md#probe-4) (friction 1) could scan text only by self-recursion that takes one character with `StringSlice` per call. That is quadratic, because each slice walks from the start, and it overflows the stack at about 7,700 characters.

## Decision

**`StringCodePoints(text): List<String>`.** Each code point of `text` as a one-code-point String, in order. It runs in linear time and never fails; the empty String gives the empty list. Text is then iterated with the existing list operations, usually `ListFold(StringCodePoints(text), seed, folder)`.

**Unpaired surrogates are their own items.** A valid surrogate pair is one item. An unpaired surrogate code unit is a one-unit item, exactly as `StringLength` counts it and `StringSlice` positions it. So `ListLength(StringCodePoints(t)) = StringLength(t)`, item `i` equals `StringSlice(t, i, i + 1)`, and concatenating the items gives back `t`.

**Recursion depth is documented, not raised.** A nested `DirectCall` is a frame on the runner's thread stack. The bound depends on the function's frame size (several thousand nested calls; probe 4's five-parameter scan overflowed at about 7,700) and is documented in the [language subset](../reference/language-subset.md) and [expression authoring](../specs/expression-authoring.md#selfcall). `runPure` now reports an overflow with a message that says the recursion was too deep and names `ListFold` over `StringCodePoints` as the alternative, instead of only "Stack overflow."

## Reason

**A list of Strings over new character or index types.** There is no Char or code-point Int type, and adding one would need new literals, comparisons and run values. One-code-point Strings compose with everything that exists: `StringEqual`, `StringLessThan`, `StringConcat`, `ValueEqual`, and the list callbacks. Python's `for ch in text` yields one-character strings the same way.

**Matching `StringLength` for unpaired surrogates.** Replacing an unpaired surrogate with U+FFFD, or failing, would break the identities above and make `StringCodePoints` the only String operation that can lose or reject text. Keeping the code unit keeps the operation total and consistent with counting and slicing.

**No deeper stack.** Running the function on a thread with a larger stack would move the limit, not remove it, and it would make the limit depend on the runner rather than on the emitted program. Iterating over a list has no depth limit at all.

## Consequences

- `StringCodePoints` follows the usual path through the semantic graph, validation, snapshot and Nyason codecs (`stringCodePoints`, `string-code-points`), typed semantic IR, execution IR, the C# backend, the viewer (`code points`), the AuthoringSession wire recipe (`stringCodePoints`) and the agent recipe reference.
- The list holds one String per code point, so it uses more memory than the text. For text within `runPure`'s 65,536-code-unit argument limit that is small.
- `runPure` cannot carry an unpaired surrogate in a String, so tests check that rule from a String literal.

## Revisit conditions

- **Grapheme clusters:** a consumer must iterate user-perceived characters (for example a letter with combining marks) rather than code points.
- **Streaming:** a consumer iterates text large enough that materializing one String per code point matters.
