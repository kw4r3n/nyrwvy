# Bool operators, and deferring Map

- **Status:** Accepted
- **Date:** 2026-09-28
- **Roadmap:** [post-M2 immediate execution queue](../plans/post-m2-roadmap.md#immediate-execution-queue), order 5

This decision settles order 5 of the post-M2 queue: whether Bool and/or/not and a Map deserve dedicated constructs. The implemented surface is described by the [language subset](../reference/language-subset.md) and the [expression authoring specification](../specs/expression-authoring.md), which remain the authority once implemented.

## Decision

Add three dedicated Bool constructs:

| Construct | Meaning | Checkable expansion |
| --- | --- | --- |
| `BoolNot(operand)` | Logical negation of one Bool operand. | `Conditional(operand, false, true)` |
| `BoolAnd(left, right)` | `right` is evaluated only when `left` is true. | `Conditional(left, right, false)` |
| `BoolOr(left, right)` | `right` is evaluated only when `left` is false. | `Conditional(left, true, right)` |

`left` and `operand` are ordinary Bool operands of the enclosing graph. The `right` operand of `BoolAnd`/`BoolOr` is a nested value graph with the same scope, typing and effect rules as a `Conditional` branch: static effect checks cover it without hoisting its runtime effects into the parent graph, and it is evaluated at most once and only when needed. All operands and results are Bool.

Do not add a Map or Set type now. Do not add Bool equality now.

## Reason

**Bool operators.** Nested `Conditional` already expresses and/or/not with short-circuit evaluation, so the gap is not expressiveness. It is review and authoring cost: `a || b` appears as `if a then true else b` and `!a` as `if a then false else true`, so a reviewer must recognise the pattern to see the intent. The [dedicated-construct rule](../concepts/semantic-independence.md#dedicated-constructs-and-expansion) already accepts `ListMap`, `ListFilter`, `ListAny` and `ListAll` on exactly this ground: expressible with an existing construct, but review reads the intent directly. Bool operators are basic language parts rather than a workload-specific feature, so they do not assume an application architecture.

Short-circuit evaluation keeps the constructs exact replacements for their conditional expansions, including when the right operand aborts, invokes a capability or calls a function that fails. Strict evaluation of both operands would change meaning for those cases and make the construct not a faithful expansion.

Evidence for the cost is small but consistent: the verification planner's `requires_live` uses a conditional in place of an OR (recorded in its README), and the 2026-09-25 rrawrlet probe reported nested conditionals among its verbose patterns.

**Map deferred.** A Map needs a language-level key-equality and hashing contract. Nyrwvy has only per-type equality (`StringEqual`, `Int64Equal`, `Float64Equal`); structural equality of products, unions, Option, Result and List exists only as an unspecified C# backend detail, and Float64 NaN complicates any general contract. The evidence is also thin: the in-repository keyed lookup is a first-match `ListFold` search, and the upsert pattern exists only in the external probe. `List` of entries with a fold remains adequate for the observed sizes.

**Bool equality deferred.** No observed program needed it; `BoolNot` with a conditional expresses it.

## Consequences

- The constructs follow the usual path through the semantic graph, validation, snapshot and Nyason codecs, typed semantic IR, execution IR, the C# backend, the viewer, and the exact-ID and name-resolved recipe wire forms. Recipe names are `boolNot`, `boolAnd` and `boolOr`.
- Execution IR may lower `BoolAnd`/`BoolOr` to the same branch shape as their conditional expansions. The semantic graph keeps the dedicated construct, so review and inspection show the intent.
- Existing snapshots keep their conditionals; nothing rewrites them.

## Revisit conditions

- **Map:** a second consumer needs keyed lookup or replace-or-append, or measured fold cost matters. The smallest candidate is a Map with String or Int64 keys, which needs no new equality contract. This condition was met on 2026-09-28; the revisit, which keeps Map deferred with a new condition, is recorded in [task fields, list indexing, and Map revisited](task-fields-list-indexing-and-map.md).
- **Bool equality:** a real program needs it beyond what `BoolNot` and a conditional express readably.
